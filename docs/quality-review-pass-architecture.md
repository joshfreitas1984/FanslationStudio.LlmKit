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
behavior change anywhere in the pipeline.

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
   template, fragments)` — compares the freshly computed effective text against the stored
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
6. Parses the response: `SCORE: <0-100>` (required — `ScoreLineRegex`) and `CORRECTED: <text|NONE>`
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
fragments)`** — recomputes the current effective text and compares it against `QcReviewedText`.
`false` means treat the column exactly as if it had never been reviewed (fall through to plain
`Translated`, ignore the score gate entirely) — never trust stale Qc data just because it happens
to be non-empty. Used identically by the QC engine itself (to decide "does this need re-reviewing"
— see step 3 above) and by every packaging path (to decide "can this be trusted right now").

`GameFileHandlingBase.MergeFilesIntoTranslatedAsync` (re-export merge) also now carries a matched
split's `Qc*` fields forward alongside `.Translated` via `CopyQcState` — both of its match paths
already require `Text` equality before considering a match, so this is only ever applied when the
underlying raw fragment genuinely hasn't changed. This is a pure efficiency fix (avoids forcing a
full corpus re-review after every re-export for lines where nothing changed) — correctness doesn't
depend on it, since `IsQcReviewFresh` already guarantees stale data is never trusted even without
this copy.

## Packaging (score-gating + freshness)

Every packaging path — `PrefabTextWorkflow.PackagePrefabTextAsync`, `DynamicStringWorkflow
.PackageDynamicStringsAsync` (both in this repo), and the downstream CSV path
(`DragonHierOverLlm/Tests/TranslationPackaging.cs`'s `PackageFinalTranslationAsync`) — applies the
same two checks per column, both gated on `IsQcReviewFresh`:

1. If fresh and `QcQualityScore < qualityReview.minAcceptableScore` → treat the column as
   not-ready-to-package (same bucket a `FlaggedForRetranslation`/unsafe/missing-translation column
   already falls into) — held back from `Files/Mod`, but `Files/Converted` is untouched, so no
   translation work is ever lost.
2. Else if fresh and `QcTranslated` is non-empty → use it in place of `Translated` (for a templated
   column, this bypasses `Reconstruct()` for that column entirely, using the anchor's `QcTranslated`
   as the literal cell value).
3. Otherwise (never reviewed, or reviewed-but-stale) → falls through to ordinary `Translated`-based
   packaging, unaffected by anything Qc-related.

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

Three levels, narrowest to broadest:

- **`ResetQcRetryLimits`** — clears every column's accumulated retry counters and gives any
  column currently parked at `FailedValidation` a fresh review. Routine: run after fixing whatever
  caused a persistent rule violation (a false-positive bad word, a loosened glossary rule).
- **`ResetLeakedQcCorrections`** — resets only columns whose stored correction/rejected-
  correction contains leaked QC-protocol text (see `ContainsLeakedProtocolText`). Routine, safe to
  run any time - a no-op once the corpus is clean.
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

## Triage and fix-prompt generation (`GetQcTriageAsync`/`WriteTriageReportAsync`/`WriteFixPromptsAsync`)

`GetFlaggedQcReviews`'s output can run into the thousands on a real corpus (DragonHierOverLlm's
first full run flagged ~9,000 rows). Reading every row, or hand-marking individual lines as "ok",
doesn't scale — and the data model deliberately has no manual-approval field for that (see "Auto-
apply, gated by validation, not report-only" above). These three methods exist to turn that dump
into something a human (or a chat with an LLM) can actually work down over time, without adding any
per-line approval mechanism.

`GetQcTriageAsync(workingDirectory, textFiles, hooks, examplesPerCluster: 5, lowScoreSampleSize:
100, maxLowScorePerFile: 15)` splits `GetFlaggedQcReviews`'s output into the two populations that
need different treatment, returned as a `QcTriageResult`:

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

`WriteTriageReportAsync(workingDirectory, textFiles, hooks)` writes `TestResults/QcTriageSummary
.yaml` (headline counts), `QcTriageByReason.yaml`, and `QcTriageLowScoreSample.yaml`.

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

## Configuration (`Configuration/QualityReviewConfig.cs`)

`LlmConfig.QualityReview` (`qualityReview:` in `Config.yaml`):

- `enabled` (bool, default false) — the whole feature is a documented no-op when false.
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
