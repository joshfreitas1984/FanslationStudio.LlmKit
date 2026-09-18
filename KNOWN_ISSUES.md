# FanslationStudio.LlmKit — known-issue / design-history index

> This file is an **index only**. It is not auto-loaded into agent context (unlike
> `.github/copilot-instructions.md`, which has `applyTo: "**"`). Detailed investigation narratives
> and design-history writeups live in categorized files under `docs/` — read only the specific doc
> relevant to your current task, not this whole index. When a new investigation narrative is
> written, add it as a NEW file under `docs/` (or extend the closest-matching existing one) and add
> a one-line pointer here — never grow this file into a monolith again (this restructuring exists
> precisely because `.github/copilot-instructions.md` had grown into one).

## Reference / architecture (not bug narratives, but the "why" behind current design)

- [`docs/features/core-pipeline/ARCHITECTURE.md`](docs/features/core-pipeline/ARCHITECTURE.md) — full current-state architecture reference:
  data model, config loading, workflow entry points, translation pipeline, validation.
- [`docs/features/compound-field-splitting/compoundfieldsplitter-design.md`](docs/features/compound-field-splitting/compoundfieldsplitter-design.md) — full regex
  design/rationale for `CompoundFieldSplitter.Decompose`/`Reconstruct`, the placeholder-token
  folding design (and why an earlier post-hoc gap-merging approach was replaced), merge-across-
  re-export matching order, and the known cost of the fragment model.
- [`docs/features/prefab-text/prefabtext-workflow.md`](docs/features/prefab-text/prefabtext-workflow.md) — `TextFileType.PrefabText` design:
  a flat, row/column-less alternative to the CSV pipeline for dumped prefab/UI text.
- [`docs/features/quality-review/quality-review-pass-architecture.md`](docs/features/quality-review/quality-review-pass-architecture.md) —
  post-translation quality review pass: `TranslationSplit` Qc\* fields and the `SubIndex == 0`
  anchor convention, `QualityReviewWorkflow` mechanics, the staleness/freshness problem and its fix
  (`QualityReviewHelpers.IsQcReviewFresh`, merge preservation), score-gated packaging, and the
  per-model-family (`Qwen25`/`Glm4`) prompt design. Includes a 2026-09-16 postmortem/fix: a
  score-gate rejection in `PrefabTextWorkflow`/`DynamicStringWorkflow` used to discard a column all
  the way to raw Chinese text instead of its pre-QC `Translated` value, which shipped raw Chinese
  UI text (including a boot-screen splash notice) and broke game startup in `DragonHierOverLlm` —
  see that doc's "Packaging" section and
  [`../DragonHierOverLlm/Tests/docs/qc-run-startup-crash-investigation-2026-09-15.md`](../DragonHierOverLlm/Tests/docs/qc-run-startup-crash-investigation-2026-09-15.md).

## Bug-fix postmortems

- [`docs/investigations/translation-retry-escalation-and-fixes.md`](docs/investigations/translation-retry-escalation-and-fixes.md)
  — `TranslationService` retry/escalation mechanics, plus real-run postmortems: a correction-suffix
  prompt leak causing repeated false `Unprocessable` entries (fixed 2026-08-28),
  `ApplyAllRulesToCurrentTranslation` not applying game-specific hooks to already-translated lines
  (fixed 2026-09-08, plus a follow-up false-positive fix for tokens with an embedded digit), and a
  dropped-closing-tag bug in a runtime-color-placeholder template that `HtmlTagHelpers.ValidateTags`'
  set-based (not count-based) comparison let through — includes a reverted first-attempt fix (a new
  per-split `GameHooks.CustomTranslationExclusionRule` hook, which turned out to be the wrong layer
  since `CompoundFieldSplitter` decomposes these raw strings before any single split sees the whole
  template) before landing on the actual fix: a whole-raw/whole-result override in the consuming
  repo's existing packaging-time override mechanism (fixed 2026-09-17).

## Design history (superseded by a current-state doc above, kept for context)

- [`../DragonHierOverLlm/docs/plans/quality-review-pass.md`](../DragonHierOverLlm/docs/plans/quality-review-pass.md)
  — the original design plan for the quality review pass (spans this repo + `DragonHierOverLlm`),
  including the open questions/tradeoffs that were resolved along the way and the model-selection
  sample-run methodology. Predates most of the feature being built - for current-state reference
  use [`docs/features/quality-review/quality-review-pass-architecture.md`](docs/features/quality-review/quality-review-pass-architecture.md)
  instead.
