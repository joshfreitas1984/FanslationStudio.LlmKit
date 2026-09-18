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
> [`docs/KNOWN_ISSUES.md`](../docs/KNOWN_ISSUES.md) for the issue/postmortem index. This file is
> deliberately short — current-state operational rules only. Long rationale and investigation
> narratives belong in a linked document under `docs/`, not here.
>
> **Documentation workflow:** treat `docs/` as the durable source for feature behavior, architecture,
> rationale, investigations, and history. Do not put design history, postmortem narrative, or extensive
> rationale in source comments or auto-loaded instruction files. Keep comments for local invariants and
> non-obvious implementation constraints, and link to the relevant `docs/` topic when more context is
> useful. Do not rewrite docs during every exploratory edit or intermediate fix attempt: inspect existing
> docs first, then consolidate one documentation update when a substantial task is complete and its
> behavior is settled. Update this file only when a current-state agent rule changes, and update
> `docs/README.md`/`docs/KNOWN_ISSUES.md` indexes when links or durable topic coverage require it.
> If it is unclear whether a finding deserves a feature guide, architecture note, plan, or investigation,
> ask the user before creating or expanding documentation.
> **Documentation placement:** put current feature behavior in `docs/features/`, durable design or
> implementation plans in `docs/plans/`, and investigations, incident analysis, and postmortems in
> `docs/investigations/`. Keep `docs/KNOWN_ISSUES.md` as an index only; do not put the investigation
> narrative there.
>
> **Reverse-engineering rule:** when a substantial investigation produces reusable knowledge, capture
> the settled finding in the appropriate `docs/` topic before finishing the task, even if not explicitly
> requested. Do this once at task completion, not after every read or hypothesis. Findings that only
> exist in chat history are lost for future sessions. This applies even when investigating LlmKit-internal
> behavior from a downstream repo's session — record it here, not in the downstream repo's own notes,
> since this is the sibling repo the logic actually belongs to.

## The golden rule

The `Line → Splits → (Templates)` hierarchy (`Support/TranslationLine.cs`,
`Support/TranslationSplit.cs`, `Support/FieldTemplate.cs`) is the contract every downstream project
depends on. **Extend it with new optional fields (safe defaults), never change its shape** — old
serialized YAML in a downstream repo's `Files/Converted/*.yaml` must keep deserializing correctly.
See [`docs/architecture/ARCHITECTURE.md`](../docs/architecture/ARCHITECTURE.md) for the full current data model, config
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
`Reconstruct`. See [`docs/features/compound-field-splitting/compound-field-splitting.md`](../docs/features/compound-field-splitting/compound-field-splitting.md)
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
- See [`docs/investigations/translation-retry-escalation-and-fixes.md`](../docs/investigations/translation-retry-escalation-and-fixes.md)
  for full retry/escalation mechanics and real-run bug-fix postmortems.

## `TextFileType.PrefabText` — flat, row/column-less files

Game-agnostic handling for a dumped list of hardcoded UI/prefab text (one string per line, no CSV
structure). See [`docs/features/text-handling/prefab-text-workflow.md`](../docs/features/text-handling/prefab-text-workflow.md) for the full
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
per-model-family like `BaseSystemPrompt`, not a shared/generic file. `GameHooks.
CustomQcExclusionRule` lets a per-game rule keep a column out of the pass entirely (before any LLM
call) when it looks like prose but is actually a machine-readable record (e.g. a dialogue-choice
entry with an embedded function-routing suffix) - see
[`docs/features/translation-pipeline/quality-review-pass.md`](../docs/features/translation-pipeline/quality-review-pass.md) for the
full design, including guidance for writing a new exclusion rule.

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
- `GameHooks.CustomQcExclusionRule` — per-game rule deciding whether a column should be kept out of
  the quality review pass entirely, checked once per column before any QC LLM call (see
  `docs/features/translation-pipeline/quality-review-pass.md`).
