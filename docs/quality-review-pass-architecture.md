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
     `QcReviewedText` set, `FlaggedForQcReview = score < minAcceptableScore`.
   - A correction is proposed → restored via `tokenReplacer.Restore`, then run through the
     **validation gate**: `LineValidation.CheckTransalationSuccessful` (the exact same structural
     checks — placeholder/tag preservation, banned phrases, length sanity — a normal translation
     attempt goes through) plus a QC-specific **glossary-drift check**
     (`CheckGlossaryDrift`): every glossary term whose `Raw`/`RawSimplified`/`RawTraditional`
     matched in the raw source must still have its `Result` (or an allowed alternative) present in
     the corrected text, or the correction is rejected outright regardless of what the generic
     checks say.
     - Gate passes → `QcStatus = Corrected`, `QcTranslated` = the validated correction.
     - Gate fails → `QcStatus = FailedValidation`, `FlaggedForQcReview = true`,
       `QcRejectedCorrection`/`QcFailureReason` recorded, **`Translated` is left untouched** — a
       rejected correction is never applied.

`GetFlaggedQcReviews(workingDirectory, textFiles)` — reporting helper mirroring
`GameFileHandlingBase.GetFailedTranslations`'s `(Text, Translated, Reason)` shape, scanning for
`FlaggedForQcReview` instead of `FlaggedForRetranslation`, so a human reviewer gets the same
before/after report tooling they already know how to read.

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
downstream repo's numbered `"3a. RunQualityReviewPassSample"` test fact and the plan doc's "Sample
run before committing to a full-corpus pass" section for the full methodology and reasoning
(expected corpus size, why a full run is a many-hour job regardless of model choice, what to look
for in the sample's results).
