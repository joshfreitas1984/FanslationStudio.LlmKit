# Canonical `Config.yaml` shape

Part of the [canonical downstream project shape](canonical-project-shape.md). Source of truth:
`DragonHierOverLlm/Files/Config.yaml` as of 2026-09-16 (reproduced below with per-field notes).
Field *semantics* (what each one actually does at runtime) are owned by
`FanslationStudio.LlmKit`'s config-loading code and `ARCHITECTURE.md`/
`../features/translation-pipeline/quality-review-pass.md` — this doc only documents the top-level *shape* a
downstream repo's `Config.yaml` should have.

```yaml
models:
  - name: Qwen25-Standard
    modelPreset: Qwen25
    modelPresetType: Standard
    customPromptsPath:
    apiKeyPath:
    model: qwen2.5:14b-instruct
  - name: Qwen25-StructuredText
    modelPreset: Qwen25
    modelPresetType: StructuredText
    customPromptsPath:
    apiKeyPath:
    model: qwen2.5:14b-instruct
  - name: QwenQc-14B
    modelPreset: Qwen25
    modelPresetType: Standard
    model: qwen2.5:14b-instruct
  - name: Qwen38Qc
    modelPreset: Qwen38
    modelPresetType: Standard
glossaryPreset:
  usePresetChineseGlossary: true
  chineseGlossaryTypesToSupress:
    - Phonetics
useContinuousWorkerPool: true
maxConcurrency: 2
retryCount: 3
# escalationModelName: Qwen25-Standard
# escalationRetryCount: 1
batchSize: 100
skipLineValidation: false
correctionPromptsEnabled: true
translateFlagged: true
qualityReview:
  enabled: true
  modelName: Qwen38Qc
  minAcceptableScore: 60
  maxConcurrency: 2
  inlineRuleCheckRetries: 2
  autoAcceptDefectCategories: [HardToParseSeam, OtherNamedDefect, DroppedContent, DroppedStutter]
  twoStageVerificationEnabled: true
```

## Top-level shape

| Key | Required in every downstream repo? | Notes |
| --- | --- | --- |
| `models` | Yes | List of named model configs. At minimum one `Standard` entry for translation; DragonHeir carries a second `StructuredText` variant of the same model and separate `QwenQc-14B`/`Qwen38Qc` entries dedicated to QC. The **list shape** (`name`/`modelPreset`/`modelPresetType`/`customPromptsPath`/`apiKeyPath`/`model`) is canonical; the specific model names/presets (`Qwen25`, `qwen2.5:14b-instruct`, etc.) are game/project-specific placeholders, not something to copy verbatim into a new repo. `customPromptsPath`/`apiKeyPath` are commonly left blank (not every model needs a custom prompt override or API key). |
| `glossaryPreset` | Yes | `usePresetChineseGlossary` (bool) + `chineseGlossaryTypesToSupress` (list, e.g. `Phonetics`). Game-specific in content (which glossary types to suppress depends on the source language's glossary presets), but the two-field shape is canonical for any repo translating from a language LlmKit ships a preset glossary for. |
| `useContinuousWorkerPool` | Yes | Worker-pool/concurrency toggle. |
| `maxConcurrency` | Yes | Top-level translation concurrency, separate from `qualityReview.maxConcurrency`. |
| `retryCount` | Yes | Translation retry count. |
| `escalationModelName` / `escalationRetryCount` | Optional, commonly commented out | DragonHeir has both present but commented — meaning the shape/field names should exist in a starter config (as comments) for discoverability, but escalation is opt-in per project, not a required active setting. |
| `batchSize` | Yes | |
| `skipLineValidation` | Yes | |
| `correctionPromptsEnabled` | Yes | |
| `translateFlagged` | Yes | |
| `qualityReview` | Yes (block present even if `enabled: false`) | See below. |

## `qualityReview` block

| Field | Required? | Notes |
| --- | --- | --- |
| `enabled` | Yes | Every downstream repo should have this block with an explicit `enabled` value (`true` in DragonHeir's current run) rather than omitting the block — `QualityReviewWorkflow` treats a missing/disabled block as a no-op, so leaving it out entirely is indistinguishable from disabled but less discoverable for a new contributor. |
| `modelName` | Yes | Must reference a `name` from the top-level `models` list. DragonHeir's inline comment (`#QwenQc-14B #Glm4Qc-9B`) shows this is meant to be swapped between candidate QC models during tuning — keep that as a live comment convention in new configs, not just a bare value. |
| `minAcceptableScore` | Yes | Project-tuned threshold (DragonHeir: `60`); `new-translation-project`'s current starter default of `70` is a reasonable placeholder but is **not** derived from DragonHeir's real value — a new repo should expect to re-tune this from real hand-triage, not treat either number as gospel. |
| `maxConcurrency` | Yes | QC-pass-specific concurrency, independent of the top-level `maxConcurrency`. |
| `inlineRuleCheckRetries` | Yes | |
| `autoAcceptDefectCategories` | Yes as a field, but **must start empty/placeholder in a new repo** | DragonHeir's populated list (`HardToParseSeam, OtherNamedDefect, DroppedContent, DroppedStutter`) is the product of real hand-triage over that game's corpus (see `canonical-test-organization.md`'s QC triage facts, `"5. Triage Flagged Quality Review Items"` / `"6. Generate Quality Review Fix Prompts"`). A new repo must not copy DragonHeir's list — it should start empty/commented and be populated per-project from that repo's own triage output. |
| `twoStageVerificationEnabled` | Yes | |

## Required vs. game-specific, summarized

- **Required shape (structure must exist in every repo):** `models` list shape,
  `glossaryPreset`'s two fields, all top-level scalar concurrency/retry/batch settings, the full
  `qualityReview` block's field set.
- **Game-specific placeholders (structure required, values are not):** actual model
  names/presets/endpoints, `chineseGlossaryTypesToSupress` contents (or the glossary preset
  entirely, for a non-Chinese source language), `qualityReview.modelName`,
  `qualityReview.minAcceptableScore`, and — most importantly — `qualityReview.autoAcceptDefectCategories`,
  which must never be pre-populated from another repo's triage results.
