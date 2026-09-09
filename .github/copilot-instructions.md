---
applyTo: "**"
---

# FanslationStudio.LlmKit — Copilot Instructions

Shared library implementing a **Line → Splits → (Templates)** data pipeline for extracting Chinese
text out of game data files (CSV rows, dynamic strings, prefab text, etc.), sending it through an
LLM for translation, and reassembling the translated result back into the original file format
without corrupting structure. Consumed by downstream "over LLM" translation projects (e.g.
`DragonHierOverLlm`) via a **project reference**, not a NuGet package.

> See [`docs/README.md`](../docs/README.md) for the full documentation hub, and
> [`KNOWN_ISSUES.md`](../KNOWN_ISSUES.md) for the design-history/bug-fix index. This file is
> deliberately short — current-state operational rules only. Long rationale and investigation
> narratives belong in a linked `docs/*.md` file, not here.
>
> **Workflow rule:** after any significant feature or fix, update this file (only if a current-state
> rule actually changed) and add/extend a `docs/*.md` topic file for the narrative, indexed from
> `KNOWN_ISSUES.md`. Don't update either as a side effect of an unrelated change.
>
> **Reverse-engineering rule:** when you investigate how existing code here works, write down what
> you learned in a `docs/*.md` topic file before finishing the task, even if not explicitly asked —
> findings that only exist in chat history are lost for future sessions. This applies even when
> investigating LlmKit-internal behavior from a downstream repo's session — record it here, not in
> the downstream repo's own notes, since this is the sibling repo the logic actually belongs to.

## The golden rule

The `Line → Splits → (Templates)` hierarchy (`Support/TranslationLine.cs`,
`Support/TranslationSplit.cs`, `Support/FieldTemplate.cs`) is the contract every downstream project
depends on. **Extend it with new optional fields (safe defaults), never change its shape** — old
serialized YAML in a downstream repo's `Files/Converted/*.yaml` must keep deserializing correctly.
See [`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md) for the full current data model, config
loading, and workflow entry points.

## Core data model summary (`Support/`)

- `TranslationLine` — one raw line/row. `Raw` (untouched original), `Splits` (extracted fragments),
  `Templates` (reconstruction info for compound columns), `Translated` (`[YamlIgnore]`, computed at
  packaging time).
- `TranslationSplit` — one translatable fragment: `Split` (column/field index), `SubIndex`
  (position within that column when it's compound — **do not** assume `SubIndex == 0` means "the
  only fragment", always filter by `Split` first), `Text`/`Translated`, and workflow flags
  (`SafeToTranslate`, `FlaggedForRetranslation`, `FlaggedMistranslation`, `FlaggedHallucination`).
- `FieldTemplate` — `{ Split, Template }`, only for columns with more than one translatable
  fragment. Use `CompoundFieldSplitter.IsTrivialTemplate` to avoid adding a `{0}`-only template to
  a plain single-fragment column.
- `TextFileToSplit.SkipColumns` — per-file, per-column opt-out from decomposition/translation
  entirely (icon/resource-path columns etc.) — a downstream project's own configuration, this
  library has no opinion on which columns are translatable for any given game.

## CSV / compound-field parsing

Route all row parsing/rebuilding through `CompoundFieldSplitter.ParseCsvRow`/`RebuildCsvRow`
(never `line.Split(',')`) and cell decomposition through `CompoundFieldSplitter.Decompose`/
`Reconstruct`. See [`docs/compoundfieldsplitter-design.md`](../docs/compoundfieldsplitter-design.md)
for the full regex rules (what's absorbed as natural text vs. a fragment boundary) and the
per-game `CompoundFieldSplitterOptions.PlaceholderPatterns` extension point for dynamic tokens like
`#PlayerName#`.

## Translation pipeline (`TranslationService.cs`) — key current-state rules

- Backend is typically local Ollama, which serves one request at a time per model regardless of
  client concurrency — `maxConcurrency`/`BatchSize` cap in-flight requests, they don't force real
  parallel throughput against that backend.
- `TranslateSplitAsync` already retries internally (whole-cell + sentence-by-sentence correction
  rounds up to `RetryCount`, then an optional escalation attempt against a different model via
  `LlmConfig.EscalationModelName`/`EscalationRetryCount`). **Never wrap another retry loop around
  a call to `TranslateSplitAsync`** — this squares the worst-case call count.
- `LineValidation.CheckTransalationSuccessful` is the gatekeeper deciding whether a translation
  attempt needs a retry (banned phrases, placeholder/tag preservation, length/format sanity,
  leftover Chinese, per-game `CustomColumnValidator` hook). It only checks structural/format
  correctness, not translation quality/fluency.
- See [`docs/translation-retry-escalation-and-fixes.md`](../docs/translation-retry-escalation-and-fixes.md)
  for full retry/escalation mechanics and real-run bug-fix postmortems.

## `TextFileType.PrefabText` — flat, row/column-less files

Game-agnostic handling for a dumped list of hardcoded UI/prefab text (one string per line, no CSV
structure). See [`docs/prefabtext-workflow.md`](../docs/prefabtext-workflow.md) for the full
design; a consuming project's packaging step must filter `PrefabText` entries out of its CSV
reconstruction loop and call `PrefabTextWorkflow.PackagePrefabTextAsync` instead.

## Quality review pass (`Workflow/QualityReviewWorkflow.cs`) — optional, opt-in

A second, independent pass over already-translated text (`qualityReview.enabled` in `Config.yaml`)
— an LLM judges fluency/accuracy per column, optionally proposes a correction (only accepted if it
passes `LineValidation.CheckTransalationSuccessful` plus a glossary-drift check), and rates its own
confidence 0-100. Additive `TranslationSplit` fields only (`QcTranslated`, `QcStatus`,
`QcQualityScore`, etc.) — no change to the golden-rule contract. **Nothing besides
`QualityReviewWorkflow` itself resets these fields** — a retranslation or re-export can leave them
stale, so anything that would trust `QcTranslated`/`QcQualityScore` (packaging, or the QC pass
deciding whether to re-review) must first check
`Utility.QualityReviewHelpers.IsQcReviewFresh(...)`. The QC prompt (`BaseQualityReviewPrompt`) is
per-model-family like `BaseSystemPrompt`, not a shared/generic file. See
[`docs/quality-review-pass-architecture.md`](../docs/quality-review-pass-architecture.md) for the
full design.

## Testing conventions

- `Tests/` in this repo is a genuine, fast, CI-safe xUnit regression suite (pure unit tests against
  static utilities + `ScriptedLlmHandler`-mocked-HTTP tests against `TranslationService`). Safe to
  run as a batch.
- A downstream repo's own workflow tests (e.g. `DragonHierOverLlm/Tests/`) are **not** a regression
  suite — numbered, manually-run steps that mutate real working-directory state and call a live
  LLM. Never treat a run there as a pass/fail signal for a code change in this repo.
- When fixing a fragment extraction/reconstruction bug, add a targeted assertion on
  `CompoundFieldSplitter.Decompose(...)`'s `Template`/`Fragments` output, not just a round-trip
  equality check.

## Known extension points (where a new game/project plugs in)

- `TextFileToSplit[]` list (per-file config: type, glossary on/off, skip columns, prompt name).
- `CompoundFieldSplitterOptions.PlaceholderPatterns` — per-game dynamic-token regex.
- `Config.yaml` — models, batch size, retry/correction toggles, split characters/regex.
- `{ModelName}Prompts/*.txt` — override any base/dynamic prompt without forking the preset.
- `Glossary/*.yaml`, `ManualTranslations.yaml` — data-only overrides, no code changes needed.
- `LineValidation.CustomPostRepair`/`CustomColumnRepair`/`CustomColumnValidator` — per-game
  deterministic repair/validation hooks, invoked from both a live LLM call and the no-LLM-call
  rules pass (`Workflow/TranslationWorkflow.cs`'s `ApplyAllRulesToCurrentTranslation`).
