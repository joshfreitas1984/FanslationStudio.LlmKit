# Quality review pass — feature reference

> Current-state reference for the post-translation quality review (QC) feature — describes **what
> the code does today**, not the design process. For the original design rationale, the open
> questions that were resolved along the way, and the model-selection sample-run methodology, see
> [`../../DragonHierOverLlm/docs/plans/quality-review-pass.md`](../../DragonHierOverLlm/docs/plans/quality-review-pass.md)
> (that document predates most of this being built and is kept as historical planning context, not as the
> ongoing technical reference — this file is).

## What it is

A second, independent pass over already-translated text: for each column that has a real,
non-flagged translation, an LLM (typically a different, larger model than the one doing primary
translation) judges whether it's accurate and well-constructed, optionally proposes a correction,
and rates its own confidence 0-100. A proposed correction is only ever accepted if it passes the
same structural validation gate a normal translation attempt does, plus a glossary-drift check.
Nothing about the core `Line → Splits → (Templates)` contract changes — this is purely additive
fields, one new workflow class, and packaging-time behavior gated on those new fields.

Entirely opt-in: a project that never sets `qualityReview.enabled: true` in `Config.yaml` sees zero
behavior change anywhere in the pipeline. `enabled` also gates packaging, not just whether the QC
pass itself runs — see "Packaging" below.

## Data model (`Support/TranslationSplit.cs`, `Support/QcStatus.cs`)

New fields on `TranslationSplit`, all additive with defaults that preserve old behavior for
already-serialized YAML:

- `QcTranslated` (string) — the accepted correction, if any.
- `QcStatus` (`QcStatus` enum: `NotReviewed` / `Passed` / `Corrected` / `FailedValidation`).
- `QcReviewedText` (string) — the exact effective translated text that was reviewed to produce the
  current `QcStatus`. This is the mechanism behind "don't re-review every line every run" *and*
  the staleness-detection mechanism (see below) — it is compared against a freshly recomputed
  effective text every time, never assumed valid just because it's non-empty.
- `FlaggedForQcReview` (bool) + `QcRejectedCorrection` (string) + `QcFailureReason` (string) — set
  when a proposed correction is rejected by the validation gate, or when the score is below
  threshold. Follows the same convention as `FlaggedForRetranslation`/`FlaggedMistranslation` —
  recomputed on every review, not an append-only marker. Lets a human reviewer see exactly what QC
  tried and why, directly in `Files/Converted/*.yaml`, without a separate log file — mirrors
  `GameFileHandlingBase.GetFailedTranslations`'s reporting shape (see
  `Workflow.QualityReviewWorkflow.GetFlaggedQcReviews`).
- `QcQualityScore` (`int?`, 0-100) — self-rated confidence from the QC model, set on every reviewed
  column regardless of whether a correction was proposed. Treat as a relative sort key for triage,
  not a calibrated absolute metric — a small/local model's self-rating is inherently noisy.
- `QcDefectCategory` (`Support/QcDefectCategory.cs` enum: `Unknown` / `None` / `GarbledNumber` /
  `DomainTerm` / `LostIdiom` / `UntranslatedPinyin` / `DroppedContent` / `HardToParseSeam` /
  `OtherNamedDefect`) — the `DEFECT:` category the model names alongside `SCORE:`, parsed from the
  same response (see step 6 below). `Unknown` (the default) means either "never reviewed" or a
  response that predates the DEFECT-first prompt — same "not yet reviewed" convention as
  `QcQualityScore` being `null`. Exists specifically so a large flagged set can be triaged/policed
  *by category* instead of only by score — see "DEFECT categories and per-category policy" below.
- `ResetQcState()` — clears all of the above back to `NotReviewed`. Only ever called from inside
  `QualityReviewWorkflow.ReviewColumnAsync`, right before recording a fresh outcome.
  **`TranslationSplit.ResetFlags()` deliberately does NOT call this** — Qc state tracks an
  independent review axis from `FlaggedForRetranslation`/`FlaggedMistranslation`/`FlaggedHallucination`,
  which track the primary translation attempt.

### The `SubIndex == 0` anchor convention for templated columns

A plain column has exactly one split, so there's no ambiguity about where its Qc fields live. A
**templated** (compound) column has several fragments reconstructed via `{0}`/`{1}`/... — but the
QC pass reviews the *whole reconstructed cell* (see below), so a proposed correction is a single
sentence that generally can't be cleanly re-split back onto individual fragments (the same
ambiguous-reverse-mapping problem the fragment model exists to avoid elsewhere). Resolution: **the
whole-cell QC verdict for a templated column lives entirely on that column's `SubIndex == 0`
fragment** — one QC verdict per column, not per fragment. Other fragments in the same column
(`SubIndex >= 1`) never carry their own independent Qc state. Every piece of code that reads Qc
fields for a templated column (`QualityReviewWorkflow`, packaging, `QualityReviewHelpers`)
consistently looks them up on the `SubIndex == 0` fragment only — never assume `SubIndex == 0`
means "the only fragment" elsewhere in this codebase, but for Qc state specifically, it's the sole
authority for the whole column.

## `Workflow/QualityReviewWorkflow.cs`

`RunAsync(workingDirectory, textFiles, sampleSize: null)`:

1. Validates config: `qualityReview.enabled`, `qualityReview.modelName` resolves to a configured
   model, and that model has a `BaseQualityReviewPrompt` prompt (see "Prompts" below) — throws
   immediately on a misconfiguration rather than silently no-op'ing or reviewing with a missing
   prompt.
2. Loads every file's `Converted/*.yaml` up front (same pattern as `TranslationService`'s pooled
   scheduler's `PooledFileState`), then builds one **work item per column**: `line.Splits.GroupBy(s
   => s.Split)`, anchored on the `SubIndex == 0` fragment, flattened across every line in every
   file into one list.
3. If `sampleSize` is set, randomly samples that many work items (not first-N — a first-N sample
   would be biased toward whichever file happens to be enumerated first) before processing. This
   exists specifically so a candidate model can be tried on a small, representative sample before
   committing an entire run to it — see the downstream plan doc's "sample run" methodology.
4. Runs `Parallel.ForEachAsync` (bounded by `qualityReview.maxConcurrency`, falling back to
   `maxConcurrency` → `batchSize` → 20, same chain every other concurrency knob uses) over the work
   items, calling `ReviewColumnAsync` per column, with the same periodic buffered-flush-to-disk
   pattern `TranslateViaLlmAsyncPooled` uses (`TranslationService.BatchlessBuffer`/`BatchlessLog`
   reused directly, not reinvented).

`ReviewColumnAsync(config, modelConfig, client, item)` per column:

1. **Readiness check** — skips (no LLM call) if any fragment in the column is still
   `FlaggedForRetranslation`, `!SafeToTranslate`, or missing its translation. Reviewing a column
   mid-retry-loop would waste a call on text about to be replaced anyway.
2. Computes the **effective text** via `Utility.QualityReviewHelpers.ComputeEffectiveTranslatedText`
   — for a templated column, `CompoundFieldSplitter.Reconstruct(template, fragments.Select(f =>
   f.Translated))` (the exact string that would be written to the CSV today); for a plain column,
   just `anchor.Translated`. This is the **review unit**: the QC pass judges the fully reconstructed
   cell, not individual fragments in isolation, since the splitter-seam problem it exists to catch
   is a property of the whole cell.
3. **Skip if already reviewed and unchanged**: `QualityReviewHelpers.IsQcReviewFresh(anchor,
   template, fragments, qualityReview)` — compares the freshly computed effective text against the stored
   `QcReviewedText`. True means no LLM call needed. This is the "don't re-review every line every
   run" mechanism, and it's fully self-invalidating: if `Translated` changes for any reason
   (retranslation, a future re-export/merge), the next run's freshly computed effective text won't
   match the stale `QcReviewedText`, and the column gets picked up for review automatically — no
   manual invalidation anywhere.
4. **Masking**: raw text and effective translated text are both run through a fresh
   `Utility.StringTokenReplacer` (`.Replace`/`.Restore`) — the exact same masking mechanism the
   translation pipeline already uses for `#PlayerName#`-style game placeholders and `{n}` template
   slots, reused rather than inventing a second mechanism. The QC model never sees an unmasked
   dynamic token.
5. Builds the QC prompt: masked raw + masked translation + relevant glossary lines (via
   `GlossaryLine.AppendPromptsFor`, so the model knows canonical name/term mappings), sent as a
   single user message against the `BaseQualityReviewPrompt` system prompt. One LLM call
   (`TranslationService.TranslateMessagesAsync`, reused directly — no new HTTP-calling code) —
   producing both the score and the verdict in the same round trip.
6. Parses the response: `SCORE: <0-100>` (required — `ScoreLineRegex`), `DEFECT: <category|NONE>`
   (`DefectLineRegex`/`ParseDefectCategory` — optional; a response that predates the DEFECT-first
   prompt or otherwise omits/mis-formats this line just leaves `QcDefectCategory` at `Unknown`,
   it never blocks parsing the rest of the response), and `CORRECTED: <text|NONE>`
   (`CorrectedLineRegex`). A response that fails to parse the score is treated as unreviewed and
   left completely untouched (not recorded as a guess) — picked up again next run.
7. `anchor.ResetQcState()` then records the fresh outcome:
   - No correction (or `CORRECTED: NONE`) → `QcStatus = Passed`, `QcQualityScore` set,
     `QcReviewedText` set. If the score is below `minAcceptableScore`, `TryRetryForLowScore` retries
     (bounded by `QcRuleCheckFailureCount`/`MaxRuleCheckRetries`) rather than accepting outright —
     there's no correction here to lose by re-rolling, just the original `Translated`, which stays
     untouched either way.
   - A correction is proposed → restored via `tokenReplacer.Restore`, then run through the
     **validation gate**: `TranslationWorkflow.EvaluateRules` (the same shared rule list a normal
     translation attempt's result has to pass — glossary-drift, bad words, required-token/ellipsis
     checks, structural validation) plus `CheckCapitalizationRegression` (QC-specific: rejects a
     correction that flips normal casing to an all-caps "shout").
     - Gate fails → `QcStatus = NotReviewed` (retried next run) or, once
       `QcRuleCheckFailureCount` exceeds `MaxRuleCheckRetries`, `QcStatus = FailedValidation`
       (terminal, `FlaggedForQcReview = true`) — either way `QcRejectedCorrection`/`QcFailureReason`
       recorded and **`Translated` is left untouched**.
     - Gate passes → **accepted immediately**: `QcStatus = Corrected`, `QcTranslated` = the
       validated correction, `FlaggedForQcReview = score < minAcceptableScore`. Critically, a low
       score here does **not** trigger another retry — see "Postmortems" below for why re-rolling a
       validated correction on a low self-reported score was itself a bug, not a safeguard.

`GetFlaggedQcReviews(workingDirectory, textFiles)` — reporting helper mirroring
`GameFileHandlingBase.GetFailedTranslations`'s reporting shape, scanning for `FlaggedForQcReview`
instead of `FlaggedForRetranslation`, so a human reviewer gets the same before/after report tooling
they already know how to read. Groups by column (same shape as `RunAsync`'s work items) rather than
iterating raw `Splits` directly, and reports two fields that are easy to get wrong for a templated
column: `Text` is the *reconstructed whole-cell raw text* (`CompoundFieldSplitter.Reconstruct` over
every fragment's `Text`, not just the anchor fragment's own piece), and the translation field is
`QcReviewedText` — not the column's current `Translated` — since that's the exact text QC actually
judged to produce this flag (and matches what a stale review fails to match against a
since-changed `Translated`, see below).

## Staleness / freshness (`Utility/QualityReviewHelpers.cs`)

Nothing else in the pipeline calls `ResetQcState()` — in particular, a retranslation triggered by
an unrelated cause (a glossary change flags a column via
`Workflow.TranslationWorkflow.ApplyAllRulesToCurrentTranslation`, then it gets retranslated) changes
`Translated` but leaves `Qc*` fields describing the *old* translation, since `ResetFlags()`
deliberately doesn't touch them. Every place that would otherwise trust `QcTranslated`/
`QcQualityScore` must first check **`QualityReviewHelpers.IsQcReviewFresh(anchor, template,
fragments, qualityReview)`** — recomputes the current effective text and compares it against
`QcReviewedText`. `false` means treat the column exactly as if it had never been reviewed (fall
through to plain `Translated`, ignore the score gate entirely) — never trust stale Qc data just
because it happens to be non-empty. Used identically by the QC engine itself (to decide "does this
need re-reviewing" — see step 3 above) and by every packaging path (to decide "can this be trusted
right now").

`IsQcReviewFresh` also returns `false` outright, before checking anything else, when
`qualityReview.enabled` is `false` — see "Packaging" below for why this matters beyond just gating
whether the QC pass runs.

`GameFileHandlingBase.MergeFilesIntoTranslatedAsync` (re-export merge) also now carries a matched
split's `Qc*` fields forward alongside `.Translated` via `CopyQcState` — both of its match paths
already require `Text` equality before considering a match, so this is only ever applied when the
underlying raw fragment genuinely hasn't changed. This is a pure efficiency fix (avoids forcing a
full corpus re-review after every re-export for lines where nothing changed) — correctness doesn't
depend on it, since `IsQcReviewFresh` already guarantees stale data is never trusted even without
this copy.

## Packaging (score-gating + freshness)

Every packaging path — `CsvGameDataWorkflow.PackageAsync`, `JsonGameDataWorkflow.PackageAsync`,
`PrefabTextWorkflow.PackagePrefabTextAsync`, `DynamicStringWorkflow.PackageDynamicStringsAsync`
(all four in this repo) — applies the same two checks per column, both gated on `IsQcReviewFresh`:

1. If fresh and `Utility.QualityReviewHelpers.PassesQcScoreGate(QcQualityScore, QcDefectCategory,
   qualityReview)` returns `false` → the proposed `QcTranslated` correction is discarded, but that's
   ALL that's discarded: the column still packages normally on its ordinary pre-QC `Translated` text
   (step 3 below), exactly as if QC had never touched it. `PassesQcScoreGate` is the single choke
   point every packaging path shares for this decision: it passes outright if the score cleared
   `qualityReview.minAcceptableScore`, and otherwise still passes if `QcDefectCategory` is in
   `qualityReview.AutoAcceptDefectCategories` — see "DEFECT categories and per-category policy"
   below for what that second clause is for.

   **This was a real bug until 2026-09-16**: `PrefabTextWorkflow`/`DynamicStringWorkflow` used to
   treat a score-gate failure as "not-ready-to-package" and discard the column all the way down to
   raw, untranslated Chinese text (`line.Raw`/`split.Text`) — throwing away the perfectly good
   pre-QC `Translated` text along with the rejected correction.
   `CsvGameDataWorkflow`/`JsonGameDataWorkflow` never had this bug; they always fell back to
   `Translated` correctly. The bug shipped raw Chinese UI text — including an age-rating splash
   notice shown on the very first boot screen — into a real build and broke game startup. See
  `DragonHierOverLlm/docs/investigations/qc-run-startup-crash-investigation-2026-09-15.md` for the full
   diagnosis. Fixed by scoping the score-gate check to only skip the `QcTranslated` shortcut in step
   2 below, never the ordinary fragment/`Translated` reconstruction in step 3.
2. Else if fresh and `QcTranslated` is non-empty → use it in place of `Translated` (for a templated
   column, this bypasses `Reconstruct()` for that column entirely, using the anchor's `QcTranslated`
   as the literal cell value).
3. Otherwise (never reviewed, reviewed-but-stale, or `qualityReview.enabled: false`) → falls through
   to ordinary `Translated`-based packaging, unaffected by anything Qc-related.

**`qualityReview.enabled` gates packaging too, not just the QC pass.** Because step 1 is gated on
`IsQcReviewFresh` and that now returns `false` whenever `enabled` is `false`, setting
`qualityReview.enabled: false` and re-running packaging (no LLM calls) makes every column package as
if QC had never touched it — plain pre-QC `Translated` text everywhere, with
`minAcceptableScore`/`autoAcceptDefectCategories` never consulted, even for columns that already
have a stored `QcTranslated`/`QcQualityScore` from a prior run. This is the intended way to isolate
"did QC's correction introduce this defect" from "was it already there before QC touched it" — see
`DragonHierOverLlm/docs/investigations/qc-run-startup-crash-investigation-2026-09-15.md` for a worked example.
Re-enabling restores the stored Qc data exactly as it was; nothing is deleted or reset by toggling
this flag.

### Raw-text fallback: never raw Chinese for PrefabText/DynamicString

Independent of QC, a column can still be genuinely unusable at packaging time — unsafe
(`!SafeToTranslate`), flagged for retranslation, or missing its translation entirely
(`PackagingFailureReason.RawFallback`). `CsvGameDataWorkflow`/`JsonGameDataWorkflow` package these
rows with their original raw CSV cell value, since a CSV row structurally must have *something* in
every column. `PrefabTextWorkflow`/`DynamicStringWorkflow` package a flat raw-string dictionary
instead, where every entry is optional — so as of 2026-09-16 a `RawFallback` line there is **omitted
from the packaged dictionary entirely** rather than written as an explicit raw-Chinese entry.
`ReconstructLine` returns `(null, PackagingFailureReason.RawFallback, ...)` for this case; the
caller (`PackagePrefabTextAsync`/`PackageDynamicStringsAsync`) still counts it toward the reported
`RawFallback` total even though nothing is added to `results`.

This matters most for `DynamicStringWorkflow`, consumed as a runtime *substring* replacement (see
this file's own type-level doc comment): an explicit raw-Chinese dictionary entry risks the
runtime's own "does this text still contain untranslated Chinese" check matching the very entry
that was deliberately left as Chinese, re-triggering whatever that check does on every match — a
replace-and-recheck cycle that can loop. Omitting the entry instead means no substitution happens at
all: visually identical to a raw-Chinese entry (the original text is untouched either way), but with
no re-match risk. See
`DragonHierOverLlm/docs/investigations/qc-run-startup-crash-investigation-2026-09-15.md` for the real
incident this generalizes from.

**Known limitation** (`DynamicStringWorkflow`): a single-fragment template's "bare label" dictionary
entry (needed for NPC dialogue-option buttons — see that file's own doc comments) can't be derived
from a whole-cell QC correction without the same ambiguous reverse-mapping problem the anchor
convention exists to avoid, so a QC-corrected multi-part dynamic-string line loses its bare-label
entry. The full reconstructed entry still packages correctly regardless.

## Per-column exclusion (`GameHooks.CustomQcExclusionRule`) — keeping structurally opaque text out of QC entirely

Some columns look like ordinary translated prose to the QC pass but are actually machine-readable
records - a raw/effective-translated cell that's really `"{label};FunctionName"` or similar, where
only part of the string is real language and the rest is an opaque runtime identifier. A QC model
has no way to know that, and two problems follow: it can waste a call trying to "fix" formatting
around the non-language part, and - because packaging accepts an accepted `QcTranslated` for a
templated column as the literal final cell value (bypassing `CompoundFieldSplitter.Reconstruct()`
entirely, see "Packaging" above) - a corrupted correction has nothing to catch it unless the
consuming project has separately registered a `GameHooks.CustomColumnValidator` for that exact
file/column.

`GameHooks.CustomQcExclusionRule` (`Func<TextFileToSplit, int?, string, bool>`, receiving
`(textFile, column, rawText)`) lets a downstream project keep a column out of the QC pass entirely,
decided once per column while `QualityReviewWorkflow.RunAsync` builds its work-item list - BEFORE
any LLM call, not after. `rawText` is already the fully reconstructed whole-cell raw text (same
`CompoundFieldSplitter.Reconstruct` call `ReviewColumnAsync` itself uses), so the hook sees exactly
what the QC model would have been shown. Returning `true` means the column never becomes a
`QcWorkItem` this run - it isn't `Skipped`-and-retried the way an in-progress retranslation is, it
simply never enters the pipeline. This is deliberately a separate, cheaper mechanism from
`CustomColumnValidator`: excluding a column removes the risk (and the LLM cost) altogether, rather
than reviewing it and hoping a validator catches a bad correction after the fact.

### Writing one for a new project

The pattern that's worked in practice (see DragonHierOverLlm's `GameFileHandling.
ExcludeFunctionRoutedDynamicStringFromQc` for the real example): identify a raw-text signature that
reliably marks "this is a machine record, not prose" for your game - ideally a structural character
your game's raw dumps essentially never contain in genuine translated text (DragonHierOverLlm uses
a literal ASCII `;` in a `DynamicStringsIL2CPP` entry, since that game's dialogue-choice/function-
routing convention is `"{choiceText};FunctionName;params..."` and ordinary Chinese text never
contains an ASCII semicolon) - then scope the check to the file/`TextFileType` where that
convention actually applies, not a blanket rule that could misfire on an unrelated file where the
same character is legitimate elsewhere. Before trusting a candidate pattern, sample the real corpus
for every raw entry containing it and eyeball a chunk of the results - the goal is a rule that's
both narrow enough to leave ordinary prose alone and broad enough to catch every real instance of
the structural shape (a stricter "must look exactly like `;Identifier`" regex risks missing
structural variants, e.g. a trailing bare `;` with nothing after it, or a multi-`;`/`&`-separated
numeric-parameter record).

Register it on the same `GameHooks` instance already passed to every `QualityReviewWorkflow`/
`TranslationWorkflow` call site (`CustomPostRepair`/`CustomColumnRepair`/`CustomColumnValidator`
live there too) - no separate wiring needed.

## Resetting Qc state

Five levels, narrowest to broadest:

- **`ResetQcRetryLimits`** — clears every column's accumulated retry counters and gives any
  column currently parked at `FailedValidation` a fresh review. Routine: run after fixing whatever
  caused a persistent rule violation (a false-positive bad word, a loosened glossary rule).
- **`ResetLeakedQcCorrections`** — resets only columns whose stored correction/rejected-
  correction contains leaked QC-protocol text (see `ContainsLeakedProtocolText`). Routine, safe to
  run any time - a no-op once the corpus is clean.
- **`ResetCorrectedQcState(workingDirectory, textFiles)`** — resets every column currently at
  `QcStatus.Corrected` back to `NotReviewed`, regardless of its stored score. Status-based
  counterpart to `ResetLowScoreQcState` below, for when the scoring itself changed enough that
  every existing `Corrected` verdict's score is suspect no matter which side of a threshold it
  lands on (a `scoreThreshold` on `ResetLowScoreQcState` can't express this if a rubric fix
  overcorrects scores upward instead of down - see postmortem #4). Leaves `Passed`/
  `FailedValidation`/rejected columns untouched.
- **`ResetLowScoreQcState(workingDirectory, textFiles, hooks, scoreThreshold: null)`** — resets
  only columns whose current `QcQualityScore` is below `scoreThreshold` (default:
  `qualityReview.minAcceptableScore`), leaving every already-accepted column with an acceptable
  score untouched. Use this after changing how the score is judged - tuning
  `BaseQualityReviewPrompt.txt`'s scoring rubric, or switching `qualityReview.modelName` to a model
  that scores on a different scale - so columns sitting below threshold under the OLD calculation
  get a genuinely fresh score under the new one, without paying for a full corpus re-review. A
  rejected-correction column (`Reason` set) is never touched here - its `QcQualityScore` is already
  `null` (see the `RejectedByGate` branch in `ReviewColumnAsync`), so it falls outside the score
  comparison entirely; use `ResetQcRetryLimits` for those. Pass a wider `scoreThreshold` (e.g. 101)
  if the change is broad enough that even comfortably-passing scores are suspect.
- **`ResetNonAutoAcceptedQcState(workingDirectory, textFiles, hooks)`** — the DEFECT-category
  counterpart to `ResetLowScoreQcState`: resets every currently-flagged column whose
  `QcDefectCategory` is NOT in `qualityReview.AutoAcceptDefectCategories` back to `NotReviewed`,
  leaving auto-accepted-category columns and everything not flagged in the first place untouched.
  `QcDefectCategory.Unknown` always lands in this bucket (a line whose response predates the
  DEFECT-first prompt, or otherwise failed to parse a `DEFECT:` line), so this doubles as the way to
  backfill DEFECT for old flagged rows - run it once, then re-run the QC pass. Also safe to re-run
  any time `AutoAcceptDefectCategories` changes (a category's hand-validated precision verdict is
  added or revised), to pull the newly-decided set back out of "flagged" one way or the other on the
  next pass. See "DEFECT categories and per-category policy" below.
- **`ResetAllQcState`** — wipes **every** column's Qc* state back to `NotReviewed`
  regardless of current status, so the next full pass reviews the entire corpus again from scratch.
  NOT routine - this is a deliberate full do-over, the same many-hours cost as an original full run.
  Use it when the QC model or prompt has changed enough that already-recorded `Passed`/`Corrected`
  verdicts can no longer be trusted: `IsQcReviewFresh` only tracks whether `Translated` changed,
  never whether the model/prompt that produced an existing verdict did, so swapping models alone
  never triggers any re-review on its own. See "Postmortems" below for the case that prompted adding
  this - a `Passed` column reviewed before the omitted-subject prompt rule existed, or a `Corrected`
  column that may have been through the low-score-discard bug, both look identical (and equally
  "trustworthy") to a freshness check that only looks at whether the underlying translation changed.
- **`ResetStutterAffectedQcState`** — narrower, cheaper alternative to `ResetAllQcState` for a
  change that only affects a small, mechanically-detectable subset of the corpus: resets every
  column whose SOURCE contains a Chinese stammer/stutter pattern (e.g. `思、思阁主`, `你、你、你……`)
  back to `NotReviewed`, regardless of its current score/status. Added alongside the `DROPPED_STUTTER`
  defect category - before that existed, a dropped stutter had no named defect to score against and
  almost always passed QC silently at a high score, so this targets exactly those columns for a
  fresh look instead of paying for a full-corpus re-review to catch a narrow pattern. Finds
  candidates via a plain regex scan (no LLM call), so it's cheap to run even on a large corpus.

## Triage and fix-prompt generation (`GetQcTriageAsync`/`WriteTriageReportAsync`/`WriteFixPromptsAsync`)

`GetFlaggedQcReviews`'s output can run into the thousands on a real corpus (DragonHierOverLlm's
first full run flagged ~9,000 rows). Reading every row, or hand-marking individual lines as "ok",
doesn't scale — and the data model deliberately has no manual-approval field for that (see "Auto-
apply, gated by validation, not report-only" above). These three methods exist to turn that dump
into something a human (or a chat with an LLM) can actually work down over time, without adding any
per-line approval mechanism.

`GetQcTriageAsync(workingDirectory, textFiles, hooks, examplesPerCluster: 5, lowScoreSampleSize:
100, maxLowScorePerFile: 15, defectCategorySampleSize: 40)` splits `GetFlaggedQcReviews`'s output
into the populations that need different treatment, returned as a `QcTriageResult`:

- **`ByReason`** — every row where `Reason` is set (a proposed correction the validation gate or
  glossary-drift check rejected, so it was never applied), grouped by the exact reason string,
  most-common cluster first. These cluster hard around a handful of root causes in practice (a
  bad-word false positive, a missing glossary term, the QC prompt ignoring an instruction) — fixing
  one root cause (prompt wording, a glossary rule, a bad-word entry) clears a whole cluster at once
  via `ResetQcRetryLimits`/`ResetLeakedQcCorrections` + a re-run, not one row at a time.
- **`LowScoreSample`** — rows with no `Reason`, just `Score` below `minAcceptableScore`. The
  correction (if any) already passed validation and was applied — this is purely QC's own
  self-rated confidence, which is frequently miscalibrated (see "Postmortems" #3 below). A small,
  deterministic (lowest-score-first, capped per file so one noisy file can't crowd out the rest)
  sample lets a human spot-check whether the score is trustworthy instead of reading every such row.
- **`ByDefectCategory`** — every flagged row grouped by `QcDefectCategory`, most-common category
  first, each with the category's total `Count` plus a `defectCategorySampleSize`-capped random
  sample (fixed seed, so repeated triage runs against the same flagged set draw the same sample -
  otherwise "did precision improve" would be impossible to tell apart from "different rows got
  sampled"). See "DEFECT categories and per-category policy" below for what this feeds.

`WriteTriageReportAsync(workingDirectory, textFiles, hooks)` writes `TestResults/QcTriageSummary
.yaml` (headline counts, including a per-category count breakdown), `QcTriageByReason.yaml`,
`QcTriageLowScoreSample.yaml`, and `QcTriageByDefectCategory.yaml`.

`WriteFixPromptsAsync(workingDirectory, textFiles, hooks, examplesPerCluster: 8)` writes
`TestResults/QcTriagePrompts.md` — one markdown section per reason cluster (a templated root-cause
diagnosis request plus concrete raw/kept-translation/rejected-correction examples) and one section
for the low-score sample (asking whether the low scores look like genuine problems or an overly
harsh rubric on short/idiomatic phrases). This is deliberately a **prompt generator, not a fix
generator** — it produces text meant to be pasted into a chat with an LLM one section at a time, not
an automated pipeline that edits prompt files itself. The actual fixes (`BaseQualityReviewPrompt
.txt` wording, a glossary rule, `qualityReview.minAcceptableScore`) are small and judgment-heavy
enough that a human should read the examples and apply the change themselves.

### Wiring this into a new project

Both methods take exactly the same `(workingDirectory, textFiles, hooks)` signature every other
`QualityReviewWorkflow` method already does, and produce the same `TestResults/*` output regardless
of which game's corpus they're reading — no per-project glue needed beyond two thin `Fact` wrappers.
Add them to your project's QC workflow test file right after your project's "find flagged" step
(see DragonHierOverLlm's `Tests/QualityControlWorkflowTests.cs` for the reference wiring):

```csharp
[Fact(DisplayName = "Triage Flagged Quality Review Items")]
public async Task TriageFlaggedQcReviews()
{
    await QualityReviewWorkflow.WriteTriageReportAsync(GameFileHandling.WorkingDirectory, TextFileConfiguration.TextFilesToSplit, GameFileHandling.Hooks);
}

[Fact(DisplayName = "Generate Quality Review Fix Prompts")]
public async Task GenerateQcFixPrompts()
{
    await QualityReviewWorkflow.WriteFixPromptsAsync(GameFileHandling.WorkingDirectory, TextFileConfiguration.TextFilesToSplit, GameFileHandling.Hooks);
}
```

The repeatable loop this supports: run the QC pass → find flagged → triage → generate fix prompts →
paste one `QcTriagePrompts.md` section at a time into a chat → apply whatever fix comes back by hand
→ `ResetQcRetryLimits`/`ResetLeakedQcCorrections` as appropriate → re-run the QC pass → re-run
triage to confirm the cluster shrank (or the low-score sample improved). Both write steps are cheap
and side-effect-free (no LLM calls, just re-reads of what `GetFlaggedQcReviews` already computes),
so re-running them as often as useful costs nothing.

## DEFECT categories and per-category policy

`QcQualityScore` alone doesn't scale as a gating signal once a corpus's flagged set gets into the
thousands - a downstream project's real investigation (DragonHierOverLlm's
`docs/investigations/qc-qualityscore-noise-investigation.md`) found a 75-91% false-positive rate among
flagged lines, with no single prompt/config lever able to move that aggregate number. The DEFECT
category (`QcDefectCategory`, parsed per "Data model" above) exists so the flagged set can be
stratified and policed *by category* instead of treating every flagged line identically:

1. **Triage** (`GetQcTriageAsync`/`WriteTriageReportAsync`, above) groups every flagged row by
   `QcDefectCategory` and writes a capped random sample per category to
   `TestResults/QcTriageByDefectCategory.yaml`, plus per-category counts to
   `QcTriageSummary.yaml`.
2. **Hand-validate a sample per category** (outside this library - a human, or an LLM chat, reading
   each sampled row's `text`/`qcReviewedText`/`qcTranslated`, comparing `qcReviewedText` - the
   *original* pre-QC translation, see `TranslationSplit.QcReviewedText`'s doc comment - against
   `qcTranslated` - QC's *proposed correction* - to judge whether a genuine defect was caught and
   fixed) and compute a rough precision (genuine defects / sample size) for each category.
3. **Set `qualityReview.autoAcceptDefectCategories`** in `Config.yaml` to the categories that came
   back at or near 0% precision - packaging then trusts `QcTranslated` for those wholesale
   (`PassesQcScoreGate`, "Packaging" above) instead of holding every such line back for human
   review. Categories with meaningfully higher precision are left off the list and stay in the
   human-review queue (`GetFlaggedQcReviews`) - often a much smaller set than the original flagged
   total, if the noise turns out concentrated in a few categories.
4. **`ResetNonAutoAcceptedQcState`** ("Resetting Qc state" above) is the repeatable maintenance step
   for this loop: it resets every flagged column whose category isn't (yet) auto-accepted back to
   `NotReviewed`, so a fresh QC run backfills `QcDefectCategory.Unknown` rows (lines reviewed before
   the DEFECT-first prompt existed) and picks up anything newly reclassified after
   `autoAcceptDefectCategories` changes.

Not every category needs identical treatment even among the "not auto-accepted" set - a category
can have low precision (mostly false positives) yet still be judged too risky to blanket-accept, if
the rare true positive that does show up is a correction that would make a fine translation *worse*
rather than merely waste a reviewer's time on a non-issue. That's a project-specific risk judgment
made when deciding what goes into `autoAcceptDefectCategories` - this library only provides the
mechanism, not the policy.

## Two-stage DEFECT verification and scoring (opt-in, `twoStageVerificationEnabled`)

**As of the fix in postmortem #6, call 1 never scores at all** - it only drafts `DEFECT`/`CORRECTED`.
Scoring, defect confirmation, and (when needed) repair are three separate, single-purpose calls, so
that no call ever grades a fix it wrote itself:

1. **Call 1** (`GetLlmVerdictAsync`) - drafts `DEFECT` + `CORRECTED` only.
2. **Call 2** (`GetVerificationVerdictAsync`) - only runs for a named DEFECT (never
   `NONE`/`Unknown`). Shown SOURCE, the *original* TRANSLATION (never a repair attempt's own
   reasoning), the claimed `DEFECT` token, and the current candidate as `PROPOSED CORRECTION` (call
   1's draft, or a later repair attempt) - confirms/rejects/recategorizes the claim and grades
   *that specific candidate*, never drafting one itself:
   ```
   DEFECT: <NONE if CLAIMED DEFECT doesn't hold up, the same token if it does, or a different DEFECT token if miscategorized>
   SCORE: <0-100, irrelevant if DEFECT is NONE>
   ```
3. **Call 3** (`GetCorrectionRepairAsync`) - only runs when call 2's score is below
   `minAcceptableScore` and repair attempts remain (bounded by `maxScoreRepairIterations`, default
   2). Given the *confirmed* defect and the current low-scoring candidate, writes ONE improved
   `CORRECTED` targeting only that defect - no score, no re-derivation:
   ```
   CORRECTED: <an improved fix, or NONE if it can't do better than the previous attempt>
   ```

`FinalizeVerdictAsync` is the loop: score via call 2 → if `DEFECT: NONE`, done (fixed `Score = 100`,
matching the same "nothing's wrong" convention used for call 1's own `NONE`); if the score clears
`minAcceptableScore` or repair attempts are exhausted, accept the current candidate; otherwise repair
via call 3 and **loop back to call 2 to re-score the new candidate - every iteration, not just the
first**, so nothing ever grades its own rewrite, including across repairs. Deliberately reuses the
exact `DEFECT:`/`SCORE:`/`CORRECTED:` vocabulary call 1 already produces reliably rather than
distinct labels per call - an earlier version of this prompt invented a `VERDICT:` token for call 2,
and a real production response came back `VERDICAT:` (a one-off model typo of a label it had never
been asked to produce before), silently falling back to call 1's own verdict every time it happened.

`GetLlmVerdictAsync`'s own `DEFECT: NONE` (nothing to correct at all) skips call 2 and call 3
entirely, regardless of this flag - fixed `Score = 100`, no extra calls, since there's nothing to
score or repair.

**When `twoStageVerificationEnabled` is false, or either `BaseQualityReviewVerificationPrompt` or
`BaseQualityReviewCorrectionRepairPrompt` is missing for the model**, a column call 1 corrects is
still accepted (the same validation gate applies either way) but with `Score = null` -
`TranslationSplit.QcQualityScore` stays unset and `FlaggedForQcReview` is always `true`. There is no
fallback self-score to use in this case, since call 1 was never asked to produce one - this is a
real behavior change from before postmortem #6, where the flag being off meant call 1's own
(self-graded, and as postmortems #4/#5/#6 found, unreliable) score was trusted directly.

Cost: up to `maxScoreRepairIterations + 1` calls to call 2, interleaved with up to
`maxScoreRepairIterations` calls to call 3 - but only for columns call 1 flags with a named defect
(~10-15% of a corpus in practice), and zero extra calls for everything else (call 1 no longer spends
any reasoning on a score nobody will use for those either). Prompt files:
`BaseQualityReviewVerificationPrompt.txt` and `BaseQualityReviewCorrectionRepairPrompt.txt`, one per
model family (`BaseFiles/<preset>/Prompts/`), loaded/merged exactly like `BaseQualityReviewPrompt.txt`
- see "Prompts: per-model-family, not a shared/generic file" below. Off by default (`false`).

## Configuration (`Configuration/QualityReviewConfig.cs`)

`LlmConfig.QualityReview` (`qualityReview:` in `Config.yaml`):

- `enabled` (bool, default false) — gates both whether the QC pass runs AND whether packaging
  trusts any already-stored `QcTranslated`/`QcQualityScore` (via `IsQcReviewFresh` — see
  "Packaging" above). Safe to flip and re-run packaging only, no LLM calls needed: turning it off
  reverts every column to its pre-QC `Translated` text without touching `Files/Converted`, and
  turning it back on restores the stored QC data exactly as it was.
- `modelName` (string) — must match a configured `models:` entry; validated at config-load time in
  `ConfigurationExtensions.GetConfiguration` (throws on a typo, same treatment as
  `LlmConfig.EscalationModelName`), but only checked when `enabled` is true.
- `maxConcurrency` (int?) — falls back to `LlmConfig.MaxConcurrency` → `BatchSize` → 20.
- `minAcceptableScore` (int, default 70) — see packaging above. Safe to change at any time and
  re-run packaging only; no LLM calls needed to see the effect, since the score is already stored
  per column.
- `maxRuleCheckRetries` (int, default 3) — see "The validation gate" above; bounds the cross-run
  `QcRuleCheckFailureCount` retry.
- `inlineRuleCheckRetries` (int, default 0) — see "Inline rule-check retries" below.
- `autoAcceptDefectCategories` (`HashSet<QcDefectCategory>`, default empty) — DEFECT categories
  hand-validated as low-precision enough that packaging should trust `QcTranslated` wholesale
  despite a low score, instead of holding the column back for human review. See "DEFECT categories
  and per-category policy" below.
- `twoStageVerificationEnabled` (bool, default false) — controls the entire scoring/repair pipeline
  for any column call 1 flags with a named DEFECT: call 1 never self-scores, so this flag decides
  whether call 2/3 run to produce a real score/repair, or whether the column is instead accepted
  with `QcQualityScore: null` and always flagged. See "Two-stage DEFECT verification and scoring"
  above.
- `maxScoreRepairIterations` (int, default 2) — bounds how many times call 3
  (`GetCorrectionRepairAsync`) attempts to improve a correction call 2 scored too low, re-scoring via
  call 2 after each attempt. Only meaningful when `twoStageVerificationEnabled` is true. See
  "Two-stage DEFECT verification and scoring" above.
- `verificationThinkingEnabled` (bool, default false) — runs call 2 (`GetVerificationVerdictAsync`)
  with Ollama's `think` mode on instead of production's normal thinking-off default. Only affects
  call 2 (never call 1 or call 3), and only when `twoStageVerificationEnabled` is true. See
  "Verification-call thinking" below.

## Verification-call thinking (opt-in, `verificationThinkingEnabled`)

Every production QC call runs with Ollama's `think` mode off (see
`LlmHelpers.GenerateLlmRequestData`'s doc comment) - both cost and both prompts' output formats
forbid a reasoning preamble. `verificationThinkingEnabled` turns thinking back on for call 2
(`GetVerificationVerdictAsync`) only - never call 1 (`GetLlmVerdictAsync`) or call 3
(`GetCorrectionRepairAsync`) - because call 2 is a narrow, single-claim judgment ("does call 1's
claimed DEFECT actually hold up, given SOURCE/TRANSLATION/the proposed correction?") rather than an
open-ended one, and only runs for the subset call 1 already flagged with a named defect (~10-15% of
a corpus), not the whole thing - the cheapest place in the pipeline to test whether reasoning
measurably improves precision.

**Budget**: reasoning tokens are generated into the SAME `num_predict`/`num_ctx` budget as the final
`DEFECT:`/`SCORE:` answer (Ollama's native `/api/chat` puts `think: true` reasoning in a separate
`thinking` response field, but it's still drawn from the same generation/context budget as the
visible `content`). With thinking off, that budget is essentially unused by anything but the two
terse output lines. With thinking on, a real reasoning trace can consume a meaningful chunk of it
before the model ever reaches `SCORE:`, and `GetVerificationVerdictAsync`'s regex parse can't tell
that apart from any other malformed response - it just logs "could not parse verification
response... treating as unscored." Rather than swap in a larger `num_predict`/`num_ctx` per-call
(which would force Ollama to reload the model with different context params on every single
verification call, since call 1/call 3 keep running with the base config in between), Qwen38's own
`BaseFiles/Qwen38/Config.yaml` `modelParams` were raised to `num_ctx: 8192`/`num_predict: 4096` (was
`4096`/`2048`) so the SAME loaded-model config has headroom for a reasoning trace on every call,
whether or not this flag is on for a given run. This is a static, always-on increase for the whole
model, not something scoped to `verificationThinkingEnabled` - if a different model family is
configured for QC, its own `BaseFiles/<Family>/Config.yaml` would need the same headroom before
turning this flag on against it.

**The reasoning trace is still always discarded before parsing** - `TranslateMessagesAsync` is
called with `enableThinking: true` but `includeThinking` left at its default `false`, so only the
final `DEFECT:`/`SCORE:` lines in `content` ever reach `ScoreLineRegex`/`ParseDefectCategory`; the
model's `thinking` field is simply never read.

Off by default, matching every other call's thinking-off default. Turning it on is a precision
experiment, not an assumed improvement - re-run the per-category hand-validation in
`docs/qc-qualityscore-noise-investigation.md` (DragonHierOverLlm repo) against a sample before
trusting that it moved anything, since it doubles call 2's token cost/latency for the flagged subset
either way.

## Inline rule-check retries (bad words only)

Before this existed, a correction that failed the validation gate got a cold restart: `ReviewColumnAsync`
set `QcStatus = NotReviewed`, and the next `RunAsync` pass called `GetLlmVerdictAsync` completely
fresh — same `SOURCE`/`CURRENT TRANSLATION`/glossary prompt, zero memory of the rejected attempt or
why it failed. For a rule violation the model would reliably repeat (e.g. reaching for "knight" to
translate a 侠-family wuxia term, tripping `TranslationWorkflow.MatchesBadWords`), this meant
burning a full extra QC pass per attempt, incrementing `QcRuleCheckFailureCount` toward
`MaxRuleCheckRetries` with the model never actually being told what it did wrong.

`QualityReviewConfig.InlineRuleCheckRetries` (default `0`, a no-op) lets `GetLlmVerdictAsync` retry
*within the same call*, in the same conversation, instead: if a proposed correction matches the
bad-words list, it appends the model's own rejected answer plus a correction turn (via
`TranslationService.AddCorrectionMessages`, the same helper the main translation pipeline's
`CorrectionPromptsEnabled` retry loop uses) naming the exact offending word(s)
(`TranslationWorkflow.FindBadWordMatches`) and asks for a different answer, up to
`InlineRuleCheckRetries` extra turns before falling back to returning the last attempt as-is.

**Scope: bad words only, deliberately.** `MatchesBadWords`/`FindBadWordMatches` are pure functions
of the candidate text alone, so they're safe to run here. Every other check in `EvaluateRules`
(glossary file-restrictions via `OnlyOutputFiles`/`ExcludeOutputFiles`, `CustomColumnValidator`,
structural validation) needs the calling column's `TextFileToSplit`/column context, which
`GetLlmVerdictAsync` deliberately doesn't have — see `LlmVerdict`'s doc comment ("before any
file/column-specific repair or validation is applied") and `ReviewCacheKey`. Those checks continue
to go through the existing cold-restart `QcRuleCheckFailureCount` cross-run retry in
`ReviewColumnAsync`/`ApplyRulesToCurrentQcTranslated`, unchanged.

**Why not thread the rejection reason through `QcRejectedCorrection`/`QcFailureReason` across runs
instead?** `GetLlmVerdictAsync`'s result is cached and shared per `ReviewCacheKey(RawText,
EffectiveTranslated, GlossaryPrompt)` (`ReviewLlmCache`) across every column — possibly in
different files — that reduces to the same triple; the caching is only correct because the call is
pure with respect to that triple. Feeding a *previous run's* per-column rejection history back into
the prompt would make the result depend on hidden state outside the cache key, so two columns
sharing an identical triple could get different verdicts depending on which one happened to fail
first. Retrying inside one `GetLlmVerdictAsync` invocation keeps every cold-start `RunAsync` pass
exactly as stateless as before, while still giving the model real, in-context feedback about its own
mistake instead of a blind restart.

**Performance**: retries only fire for the minority of corrections that hit the bad-words list on
the first attempt (rare in practice — most QC corrections pass the gate immediately), and are
sequential within the one `Parallel.ForEachAsync` worker slot already assigned to that column, so
they add latency to that one item rather than extra concurrent LLM load. Kept deliberately small and
separate from `MaxRuleCheckRetries` so the worst-case cost for a persistently-stuck line
(`InlineRuleCheckRetries` × `MaxRuleCheckRetries`) stays bounded.

## Prompts: per-model-family, not a shared/generic file

`BaseQualityReviewPrompt` is **not** a single universal prompt merged into every model — it lives
inside each model preset's own prompt set, the same tier as `BaseSystemPrompt`/
`BaseCorrectionSuffixPrompt`, because different model families can need differently-tuned wording
to reliably produce the exact `SCORE:`/`CORRECTED:` format without leaking instructions back into
 their own output (the exact failure mode `docs/investigations/translation-retry-escalation-and-fixes.md`'s
correction-suffix-leak postmortem documents for `qwen2.5:7b`). Concretely:

- `BaseFiles/Qwen25/Prompts/BaseQualityReviewPrompt.txt` — includes an explicit anti-echo
  instruction line, informed by that documented `qwen2.5` quirk.
- `BaseFiles/Glm4/Prompts/BaseQualityReviewPrompt.txt` — a new, minimal `Glm4` preset added
  specifically for this (GLM wasn't a supported preset at all before). Only ships this one prompt
  today (no `BaseSystemPrompt`/`Corrections`/`Dynamics`) since it's currently only used as a QC
  candidate, never for primary translation — add the rest under `BaseFiles/Glm4/` the same way
  `Qwen25` has them if it's ever used for real translation.

A downstream repo can still override either per-model with its own `BaseQualityReviewPrompt.txt`
under that model's `CustomPromptsPath` folder — same workspace-prompt-overrides-preset convention
every other prompt already follows.

## Sample-run support

`QualityReviewWorkflow.RunAsync`'s `sampleSize` parameter exists so a candidate model's real
speed/score-distribution/correction-quality can be judged on a small, representative sample (e.g.
~300 columns, randomly drawn across every file) before committing an entire run to it. See the
downstream repo's `RunQualityReviewPassSample`-style test fact and the plan doc's "Sample
run before committing to a full-corpus pass" section for the full methodology and reasoning
(expected corpus size, why a full run is a many-hour job regardless of model choice, what to look
for in the sample's results).

### Progress logging

`RunAsync` logs `"Quality review: N column(s) ... to consider"` up front, then, every
`TranslationService.BatchlessLog` columns *processed* (not just reviewed — every work item counts,
`Skipped` included) and again on the very last one: `"Quality review progress: {processed}/{total}
column(s) processed ({remaining} remaining) - reviewed: …, corrected: …, rejected by gate: …,
flagged: …"`. Using every processed item (not just ones that made a real LLM call) as the
denominator matters on a re-run over a large corpus: most columns are `Skipped` (already fresh),
so gating the log on real reviews alone would go quiet for long stretches even while the pass is
actively working through the file — this way the "remaining" count is always real wall-clock
progress, not just LLM-call progress.

## Investigations and regressions

Historical model-run analysis lives in [the quality-review postmortems investigation](../../investigations/quality-review-postmortems.md).
