# Quality review pass — architecture reference

> Current-state reference for the post-translation quality review (QC) feature — describes **what
> the code does today**, not the design process. For the original design rationale, the open
> questions that were resolved along the way, and the model-selection sample-run methodology, see
> [`../../DragonHierOverLlm/docs/plans/quality-review-pass.md`](../../DragonHierOverLlm/docs/plans/quality-review-pass.md)
> (that document predates most of this being built and is kept as design history, not as the
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
   `DragonHierOverLlm/Tests/docs/qc-run-startup-crash-investigation-2026-09-15.md` for the full
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
`DragonHierOverLlm/Tests/docs/qc-run-startup-crash-investigation-2026-09-15.md` for a worked example.
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
`DragonHierOverLlm/Tests/docs/qc-run-startup-crash-investigation-2026-09-15.md` for the real
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
`Tests/docs/qc-qualityscore-noise-investigation.md`) found a 75-91% false-positive rate among
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
their own output (the exact failure mode `docs/translation-retry-escalation-and-fixes.md`'s
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

## Postmortems

Real bugs found and fixed against a genuine local-model run (2026-09), kept here because each one
looks like a different kind of problem on the surface (a prompt issue, a parsing issue, a retry
policy issue) and the fix for each lives in a different layer — worth knowing which is which before
assuming a low-quality QC result must be a model-capability limit.

### 1. Omitted-subject/object mistranslation surviving QC

Chinese frequently omits the subject/object of a sentence, resolved only by surrounding dialogue
context. The primary translation pipeline translates each fragment/split independently (see
`TranslateSplitAsync`), so a fragment like `"还好还好，只是精疲力竭昏厥过去了。"` (no subject stated)
translated in isolation from the preceding line has no way to know the subject is someone else, not
the speaker — producing e.g. `"No need to worry, I just fainted from exhaustion."` when the
speaker is actually examining another character. QC reviews the whole reconstructed multi-line
cell (see "review unit" above), so it *does* have the context to catch and fix this — but the base
prompts needed an explicit rule to reliably do so. Fix: `BaseSystemPrompt.txt` and
`BaseQualityReviewPrompt.txt` (every model family) gained a rule to resolve an omitted subject/
object from context rather than defaulting to "I"/"me"/"we"/"you" for dialogue or mechanically
inheriting the preceding sentence's subject, and to prefer a neutral construction over inventing one
when context is genuinely ambiguous — never adding unsupported information.

### 2. `CorrectedLineRegex` truncating a multi-sentence correction

Even with the prompt fixed, the model's proposed correction for a multi-line cell often joined its
sentences with a real line break rather than the literal two-character `\n` escape
SOURCE/TRANSLATION use. `CorrectedLineRegex` was `Multiline` without `Singleline`, so `(.*)$`
stopped at the first physical line break — silently truncating the captured correction to its first
sentence, which then failed downstream validation as missing content regardless of how well the
model had actually resolved the translation. Fix: added `RegexOptions.Singleline`, so the capture
runs to the end of the response — `CORRECTED:` is always the last line of the prompt's two-line
output format, so nothing legitimate follows it; `ContainsLeakedProtocolText` still independently
guards the original leak concern (a model restating protocol text) this regex was originally
narrowed to prevent. `BaseQualityReviewPrompt.txt` also now asks the model to keep `CORRECTED` on
one physical line using the same literal `\n` convention, as a belt-and-suspenders nudge — not
load-bearing now that the parser tolerates either.

### 3. A validated correction discarded for a low self-reported score

Once both bugs above were fixed, a model would still reliably: (a) produce a genuinely correct fix,
(b) have that fix pass the validation gate, but (c) rate its own answer low (30-60/100) anyway.
`ReviewColumnAsync`'s acceptance path used to call the same `TryRetryForLowScore` the no-correction
(`Passed`) path uses, which discards a low-scoring result and retries from scratch — for an
already-validated correction, that meant throwing away a known-good fix and re-rolling, converging
(if at all) only once `QcRuleCheckFailureCount` exceeded `MaxRuleCheckRetries`, at which point
whatever that *last* attempt happened to produce got force-accepted — a coin flip between the same
good fix and a hedged `CORRECTED: NONE` that ships the original mistranslation. Fix: a correction
that clears the validation gate is now accepted immediately regardless of score (`FlaggedForQcReview`
still surfaces a genuinely low-confidence case for a human) — see `ReviewColumnAsync`'s final
acceptance block and `ApplyRulesToQcColumn`'s mirrored branch for an already-`Corrected` column.
`TryRetryForLowScore`/`WakeUpIfLowScore` retry-on-low-score behavior is now exclusively for the
`Passed` path, where there's nothing to lose by retrying (the original `Translated` stays either
way). Also added a `BaseQualityReviewPrompt.txt` `CONSISTENCY` rule (a low `SCORE` must come with an
actual `CORRECTED` fix, never `NONE`) as a belt-and-suspenders prompt-level nudge against the same
self-contradiction, though the retry-policy fix is what actually closes the bug.

### 4. `CONSISTENCY` rule mechanically forcing every correction below `minAcceptableScore`

A later revision of `BaseQualityReviewPrompt.txt` (all model families) tightened the `CONSISTENCY`
rule from a one-directional implication ("a low SCORE requires a CORRECTED fix") into a
bidirectional mandate: "any DEFECT value other than NONE requires SCORE below 40". That reads as
harmless self-consistency, but it silently redefined what `SCORE` means for a `Corrected` outcome -
instead of reflecting the model's actual confidence in its own fix, it became a mechanical echo of
"a defect was named," always under 40 regardless of how clear-cut or confidently-resolved the fix
was (an unambiguous glossary-term swap scored identically to a genuinely uncertain rewrite). Because
`FlaggedForQcReview = score < minAcceptableScore` for an accepted correction (see "Data model"
above), and every real-world `minAcceptableScore` sits well above 40, this made the score gate a
no-op for the entire `Corrected` population - on a downstream project's full run, 2,979 of 2,994
corrected columns (~99.5%) were flagged, not because the corrections were actually low-confidence,
but because the rubric never allowed a corrected item to score high enough not to be. This defeats
the purpose `PassesQcScoreGate`/triage exist for: distinguishing corrections worth trusting
automatically from ones that genuinely need a human look.

Fix: `CONSISTENCY` still requires `CORRECTED` to contain an actual fix whenever `DEFECT` is
non-`NONE` (never `NONE`), but no longer mandates a specific score range for that case. `SCORE`'s own
definition changed to match: confidence in `TRANSLATION` when `DEFECT: NONE`, confidence in
`CORRECTED` as the replacement otherwise - so a clear, confidently-fixed defect can score 80+, and a
low score is reserved for a fix the model isn't fully sure resolves the defect. Same fix applied to
`BaseQualityReviewVerificationPrompt.txt`'s `SCORE` line (was `80+ if DEFECT is NONE, below 40
otherwise`), since two-stage verification's call 2 shared the identical mechanical floor.

**A downstream project that already ran a full QC pass under the old rubric has every `Corrected`
column's score depressed by this bug** - re-scoring requires a fresh review, not just a prompt swap.
`ResetLowScoreQcState()` with the default threshold (`qualityReview.minAcceptableScore`) is the
targeted way to pick this up: since every `Corrected` column under the old rubric was forced below
40, all of them already sit below any realistic `minAcceptableScore`, so the default threshold
resets exactly the affected population without touching `Passed` columns (which were always
correctly 80+, unaffected by this bug) or costing a full `ResetAllQcState` re-review. Do **not**
pass an inflated `scoreThreshold` (e.g. 101) to "be thorough" - that resets every column regardless
of status, same cost as `ResetAllQcState`.

**First attempt at this fix overcorrected the other direction** - replacing the mechanical `below
40` floor with permissive wording ("a clear-cut, confidently-fixed defect can still score 80+")
gave the model a new anchor to default to instead of an actual gradient, and it collapsed to the
opposite constant: on re-review, 100% of `Corrected` columns landed at 85-100, zero below
`minAcceptableScore`, silencing `FlaggedForQcReview` for corrections just as completely as the
original bug did, in the other direction. Compounding this, the original `SCORING ANCHORS`
paragraph (a separate section from `CONSISTENCY`) still said "score low (below 40) only for a
named defect" - left unedited, it flatly contradicted the new `CONSISTENCY` wording, and the model
resolved the contradiction by picking a constant rather than actually reasoning about confidence.
Fix: `SCORING ANCHORS` and `CONSISTENCY` now both point at one explicit three-band gradient for the
`DEFECT`-named case (85-100 mechanical/unambiguous fix, 60-84 fix required real interpretive
judgment, below 60 genuine ambiguity remains) instead of either a fixed floor or a fixed permission
- a graduated scale needs concrete criteria for each band, not just "don't always score low"/"don't
always score high," or the model will still find a single constant that technically satisfies the
instruction. Applied identically to `BaseQualityReviewVerificationPrompt.txt`'s `SCORE` line.

**Every `Corrected` column reviewed under either the overly-strict or the overly-permissive
intermediate rubric needs another fresh review under the final graduated-scale prompt** -
`ResetLowScoreQcState()`'s default threshold no longer catches them once they've been overcorrected
upward to 85-100 (all above `minAcceptableScore`), and widening the threshold enough to catch them
again (101) resets the entire corpus, `Passed` columns included, at full-re-review cost. This is
exactly the gap `ResetCorrectedQcState` (see "Resetting Qc state" above) exists to close: it
targets `QcStatus.Corrected` directly rather than inferring the affected set from score.

### 5. `UNTRANSLATED_PINYIN` misapplied inside proper nouns, splitting names

Even after postmortem #4's scoring fix, a downstream project's full run kept surfacing
`UNTRANSLATED_PINYIN` corrections that partially translated one syllable of an otherwise-
transliterated personal/place/faction name - e.g. SOURCE `风间隐夜月` (a character name) originally
"Kazama Yin Ye Yue", "corrected" to "Kazama Yin Ye **Month**" (translating 月 in isolation), and several
`Hengshan ...`/`Zou Jius ...` variants doing the same to place/personal names. `UNTRANSLATED_PINYIN`'s
own definition already said "when it is **not** a proper noun," but the model wasn't reliably
recognizing that a syllable *embedded inside* a multi-character proper noun is still part of that
name, even when the syllable in isolation has an ordinary dictionary meaning (月 = moon/month). Worse,
these corrections consistently scored 85+ under the postmortem #4 gradient - the model was
"confident" in a fix that was objectively wrong, because from its perspective translating a
common-meaning character *is* the mechanical, unambiguous case that scale anchors to 85-100. This
is a precision bug in the DEFECT rule itself, not something scoring calibration alone can fix - a
correction that shouldn't have been proposed at all doesn't become safe just because it's flagged
with a lower score.

Fix: `UNTRANSLATED_PINYIN`'s definition (all three model families, base + verification prompts)
gained an explicit carve-out - it never applies to a syllable/character inside a personal, place, or
faction name that's otherwise kept transliterated; a multi-character proper noun is rendered as a
whole (fully transliterated or fully translated by convention/glossary), never split so only some
syllables get literally translated; and when uncertain whether a term is part of a name versus a
standalone common noun, treat it as part of the name and don't flag it. The verification prompt's
copy specifically tells call 2 to answer `DEFECT: NONE` when `CLAIMED DEFECT` is
`UNTRANSLATED_PINYIN` but the flagged term turns out to be inside a proper noun, so a call-1 false
positive of this shape gets caught even before a human ever sees it.

Every `Corrected` column already reviewed under the pre-fix rule needs a fresh review -
`ResetCorrectedQcState` (added for postmortem #4) already covers this, since these are all
`QcStatus.Corrected` regardless of category.

### 6. Self-grading bias survived two rubric fixes - closed structurally, not with more wording

Postmortems #4 and #5 both assumed the problem was *what the model was told* about scoring. Real
corpus samples kept disproving that: a correction that broke a name outright (postmortem #5's
"Kazama Yin Ye Month") and a correction that was genuinely clean (an unrelated hallucinated-name fix
corrected to the right pinyin) scored within 10 points of each other (85 vs 95) under the postmortem
#4 graduated scale - the score wasn't tracking correction quality at all, just clustering near the
top of whichever band the current wording anchored toward. The same call that drafts `CORRECTED` was
always the one scoring its own confidence in it (call 1 alone with the flag off; call 2 re-deriving
and self-scoring its *own* freehand rewrite with the flag on) - a model grading text it just wrote is
a well-documented self-preference bias, and no amount of rubric wording closes it, because the
wording can only change what the model *says* about its confidence, not the fact that it's grading
itself.

Fix: restructure into three single-purpose calls instead of tuning a shared one's wording a third
time - call 1 drafts and never scores at all (not even a value that gets discarded - the whole
scoring reasoning burden is removed from its prompt, which also means zero wasted tokens/reasoning
for the ~85-90% of a corpus that never gets a named defect); call 2 only grades a candidate it did
not write (either call 1's draft or a call-3 repair) and decides if a repair is needed; call 3 only
repairs, and its result always goes back through call 2 for a fresh score before being trusted -
never accepted on call 3's own say-so. See "Two-stage DEFECT verification and scoring" above for the
full mechanics. `LlmVerdict.Score` became nullable (`int?`) to support the "flag off, no fallback
score" case cleanly - a real, deliberate behavior change (always-flagged, no score) rather than
inventing a number nobody asked call 1 to produce.

**Cross-repo note**: this is shared-library code. Any other consumer of `FanslationStudio.LlmKit`
already running with `twoStageVerificationEnabled: true` picks up the new call-2/call-3 behavior
automatically on upgrade, and any regression test calling `GetLlmVerdictAsync`/
`GetVerificationVerdictAsync` directly and reading `.Score` as a non-nullable `int` will need
updating for the nullable change (confirmed to affect DragonHierOverLlm's own
`QcOmittedSubjectRegression` test as of this fix - not addressed here, since that's a change to a
different repo's test file).

Every `Corrected` column reviewed under any of the pre-#6 scoring behavior (self-scored call 1, or
call 2 re-deriving its own fix) needs a fresh review under this pipeline - `ResetCorrectedQcState`
(added for postmortem #4) already covers this.

### 7. `LOST_IDIOM` misapplied to skill/technique names, embellishing a correct literal name

Same shape of bug as postmortem #5 (`UNTRANSLATED_PINYIN` misapplied inside proper nouns), but for a
different `DEFECT` category and a different field kind: a downstream project reported SOURCE
`衡山备战心` (a skill/technique `Name` column, `衡山` = the Hengshan faction/place name, `备战心` a
plain descriptive compound - "battle-preparation heart/mind", not a real Chinese idiom or proverb),
already correctly translated literally as "Hengshan Preparations Heart", "corrected" by QC to
"Hengshan's Battle-Preparation Resolve" under `DEFECT: LOST_IDIOM`, `SCORE: 85`. There is no idiom in
the source to lose - `LOST_IDIOM`'s definition ("a lost idiom/slang meaning rendered as a literal
word-for-word gloss") was being satisfied by the model treating *any* flat/literal-sounding name as
evidence of a missed idiomatic reading, then rewriting it toward more "vivid" phrasing regardless of
whether SOURCE actually contained an idiom. Unlike postmortem #5's fix, `LOST_IDIOM` had no
proper-noun/name carve-out at all in any prompt family until this fix, so nothing stopped the model
confidently "improving" a skill name's register on every review pass it happened to sample - and,
per postmortem #6, a self-consistent 85 score gave the correction nothing to be caught by the score
gate either (above `minAcceptableScore`, and two-stage verification only re-checks a call-1 *claim*,
which the same model reliably re-confirms since its false belief that "battle-preparation heart" is
an idiom is not corrected between call 1 and call 2).

Fix: `LOST_IDIOM`'s definition (all three model families, base + verification + correction-repair
prompts) gained an explicit carve-out mirroring postmortem #5's - it never applies to a
skill/technique/move/title name that is a plain descriptive compound rather than an actual fixed
idiom or proverb; a name reading as flat literal English is not itself a defect, and rewriting it for
more "vivid" register is not a fix; when uncertain whether SOURCE actually contains a real
idiom/proverb versus an ordinary descriptive name, treat it as not an idiom and don't flag it. The
verification prompt's copy specifically tells call 2 to answer `DEFECT: NONE` when `CLAIMED DEFECT`
is `LOST_IDIOM` but SOURCE turns out to be a plain name/title, and the correction-repair prompt tells
call 3 to keep a confirmed-defect name as a faithful literal rendering rather than inventing a more
vivid one.

Every `Corrected` column with `QcDefectCategory == LostIdiom` reviewed under the pre-fix prompts
needs a fresh review. Unlike postmortems #4/#6 (where the whole `Corrected` population was suspect),
this defect is narrow enough that blanket `ResetCorrectedQcState` (which resets every `Corrected`
column regardless of category) is overkill - it has no category filter, so use it only if a targeted
per-category sweep isn't worth writing; otherwise filter by `QcDefectCategory == LostIdiom` directly
when resetting (`anchor.ResetQcState()` on just that subset) to avoid re-reviewing unrelated,
already-correct `Corrected` columns for no reason.

### 8. `OTHER_NAMED_DEFECT` (and other categories) rubber-stamping a translated name reverted to Pinyin

Same shape of bug as postmortems #5/#7 (a proper-noun handling mistake that both scores high and
survives two-stage verification unchallenged), but the direction is reversed and the category is the
catch-all rather than one with a specific definition: a downstream project (WanXiangOverLlm) reported
SOURCE `"七巧连环"左灵珠解锁红颜` where TRANSLATION already correctly rendered the puzzle name in
English (`"Seventhsilk Chain Puzzle" Zuo Lingzhu unlocks 'Crimson Beauty`), and QC "corrected" it back
to raw Pinyin (`"Qiqiao Lianhuan" Zuo Lingzhu unlocks Crimson Beauty`) under `DEFECT:
OTHER_NAMED_DEFECT`, `SCORE: 85`. This is the opposite mistake from `UNTRANSLATED_PINYIN` (which
exists specifically to catch text *left* in Pinyin) - here the model treated an already-resolved
English name as the defect and "fixed" it into Pinyin instead.

Unlike postmortems #5/#7, this couldn't be closed with a carve-out on a single named category: since
`OTHER_NAMED_DEFECT` is explicitly "something else concrete and nameable, not covered above," it has
no fixed definition to add an exception to, and the same mistake could in principle surface under
`DOMAIN_TERM` too (a translated name treated as a "mistranslated domain term"). Two-stage
verification's call 2 didn't catch it either - with no specific criteria to weigh `OTHER_NAMED_DEFECT`
against, a "concrete, nameable" change is trivially true of reverting a name to Pinyin, so call 2 had
nothing to disagree with in call 1's framing and reconfirmed it at the same mechanical-fix score band.

A corpus-wide check on this project's `Files/Converted` at the time (24,568 reviewed splits) showed
this wasn't an isolated case of miscalibration: only 5 scores in the entire corpus were below 85, and
zero columns were ever `RejectedByGate`/`FailedValidation` - consistent with postmortem #6's finding
that the model clusters near the top of whichever band the prompt anchors to, now confirmed at
production scale rather than from a handful of sampled corrections.

Fix: added a category-agnostic rule (all three model families, base + verification prompts) instead
of a per-category carve-out - `BaseQualityReviewPrompt.txt`'s `DO NOT flag/change` list gained
"reverting an already-translated proper noun/title to raw Pinyin is a regression, not a fix, no
matter which DEFECT category seems to apply," `OTHER_NAMED_DEFECT`'s definition cross-references it,
and `BaseQualityReviewVerificationPrompt.txt`'s `DEFECT: NONE` conditions gained the same rule
independent of `CLAIMED DEFECT`'s value - so call 2 rejects this shape of correction even if a future
call-1 claim uses a different category than `OTHER_NAMED_DEFECT`/`DOMAIN_TERM` to justify it.

Every `Corrected` column with `QcDefectCategory == OtherNamedDefect` (or any category) whose
`QcTranslated` reverts a name to Pinyin, reviewed under the pre-fix prompts, needs a fresh review -
`ResetCorrectedQcState` (added for postmortem #4) already covers this; there is no narrower
category-scoped reset available since the bug wasn't scoped to one category.

### 9. Model repeating its own DEFECT/CORRECTED block verbatim on longer, multi-clause corrections

A downstream project (DragonHierOverLlm, Qwen38) reported `ContainsLeakedProtocolText` rejecting
otherwise-good responses with logs like `"parsed correction for '...' contains leaked QC-protocol
text - treating as unparseable"`, and the raw response showing the *entire* `DEFECT:`/`CORRECTED:`
answer generated twice, byte-identical, back to back:

```
DEFECT: LOST_IDIOM
CORRECTED: (Is she Little Zhuge of Flying Dragon Gate?\nThis person's Spear Technique is superlative...)
DEFECT: LOST_IDIOM
CORRECTED: (Is she Little Zhuge of Flying Dragon Gate?\nThis person's Spear Technique is superlative...)
```

`ContainsLeakedProtocolText`/`CorrectedLineRegex` (postmortem #2) worked exactly as designed here -
the second block's `CORRECTED:` label is a genuine leak into the capture, correctly rejected rather
than stored. But every real occurrence shared a pattern: all were multi-clause/multi-sentence
templated corrections (`{0}\n{1}\n{2}`-shaped, several sentences joined with the literal `\n`
convention), never a short single-sentence line - i.e. the model was reliably looping and re-emitting
its whole structured answer specifically on the longer, more "creative" corrections (`LOST_IDIOM`'s
"vivid, period-appropriate wuxia/proverb-register phrasing" instruction, or a multi-clause
`DROPPED_CONTENT` restoration), not a resolution/plumbing bug. This is a generation-time degeneration
failure (the model not stopping after a complete answer), not a parsing bug - no regex change closes
it, only preventing the second block from ever being generated does.

Fix: added `stop: ["\nDEFECT:", "\nCORRECTED:"]` to Qwen38's `modelParams`
(`BaseFiles/Qwen38/Config.yaml`). Both call 1/call 2's legitimate output always starts its response
with `DEFECT:` (no preceding newline, since it's the first token generated) and call 3's always
starts with `CORRECTED:` the same way - so a stop sequence anchored to a *preceding* newline only
ever matches a second, illegitimate block starting mid-response, never a call's own legitimate first
line. Ollama halts generation the moment the stop sequence appears, so the model is cut off right as
it starts to repeat, before the leak ever reaches `ContainsLeakedProtocolText` - turning what used to
be a wasted, discarded HTTP round trip (and another full `NotReviewed` cycle before a retry
succeeds) into a clean, immediately-accepted single answer. Considered but rejected: raising
`repeat_penalty` (a corpus-wide sampling change risking translation-quality side effects elsewhere,
for a fix narrowly targeted at one structural failure mode) and per-call `num_predict`/`num_ctx`
overrides (would force Ollama to reload the model - see `verificationThinkingEnabled`'s doc comment
for why that's avoided - every time a call needed a different budget than its neighbors).

Any other model family/config wired up for QC should get the same `stop` entries if it shows the same
repeated-block symptom - this fix is config-only, not a code change, so it doesn't propagate to a
model family's `BaseFiles/<Family>/Config.yaml` automatically.

### Regression coverage

`Tests/TranslationWorkflowTests.cs`'s `"3g. QcOmittedSubjectRegression"` (DragonHierOverLlm repo)
calls `QualityReviewWorkflow.GetLlmVerdictAsync` (internal, exposed to that repo's `Tests` project
via `[assembly: InternalsVisibleTo("Tests")]` in `AssemblyInfo.cs`) directly against a fixed,
known-bad SOURCE/TRANSLATION pair from bug #1 above — independent of corpus state, so it survives
corpus edits/repackaging and needs no `ResetQcRetryLimits`/`ResetQcState` dance to re-run. It reads
`Config.yaml`'s `qualityReview.modelName` at run time, so swapping which model the project uses for
QC automatically re-validates this exact regression case against the new model with zero test
changes. Samples the model a few times (temperature is low but non-zero) and asserts no sample
reproduces the first-person mistranslation or the low-score-with-no-correction inconsistency from
bug #3 — a single call could get a differently-worded correct answer or, rarely, a still-bad one,
so any one sample reproducing the bug is treated as a real regression rather than averaged away.
