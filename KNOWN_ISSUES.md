# FanslationStudio.LlmKit — known-issue / design-history index

> This file is an **index only**. It is not auto-loaded into agent context (unlike
> `.github/copilot-instructions.md`, which has `applyTo: "**"`). Detailed investigation narratives
> and design-history writeups live in per-topic files under `docs/` — read only the specific doc
> relevant to your current task, not this whole index. When a new investigation narrative is
> written, add it as a NEW file under `docs/` (or extend the closest-matching existing one) and add
> a one-line pointer here — never grow this file into a monolith again (this restructuring exists
> precisely because `.github/copilot-instructions.md` had grown into one).

## Reference / architecture (not bug narratives, but the "why" behind current design)

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — full current-state architecture reference:
  data model, config loading, workflow entry points, translation pipeline, validation.
- [`docs/OPTIMIZATION_PLAN.md`](docs/OPTIMIZATION_PLAN.md) — scheduler comparison (pooled vs.
  batched), proposed/in-progress performance work.
- [`docs/compoundfieldsplitter-design.md`](docs/compoundfieldsplitter-design.md) — full regex
  design/rationale for `CompoundFieldSplitter.Decompose`/`Reconstruct`, the placeholder-token
  folding design (and why an earlier post-hoc gap-merging approach was replaced), merge-across-
  re-export matching order, and the known cost of the fragment model.
- [`docs/prefabtext-workflow.md`](docs/prefabtext-workflow.md) — `TextFileType.PrefabText` design:
  a flat, row/column-less alternative to the CSV pipeline for dumped prefab/UI text.

## Bug-fix postmortems

- [`docs/translation-retry-escalation-and-fixes.md`](docs/translation-retry-escalation-and-fixes.md)
  — `TranslationService` retry/escalation mechanics, plus three real-run postmortems: a
  correction-suffix prompt leak causing repeated false `Unprocessable` entries (fixed 2026-08-28),
  `ApplyAllRulesToCurrentTranslation` not applying game-specific hooks to already-translated lines
  (fixed 2026-09-08), and a follow-up false-positive fix for tokens with an embedded digit.

## In-progress feature design

- [`../DragonHierOverLlm/docs/plans/quality-review-pass.md`](../DragonHierOverLlm/docs/plans/quality-review-pass.md)
  — post-translation quality review pass. Spans this repo (new `TranslationSplit` fields, new
  `Workflow/QualityReviewWorkflow.cs`, packaging changes) and `DragonHierOverLlm` (config, a new
  numbered manually-run test step). Design document; check its own "Status" line for current
  implementation progress.
