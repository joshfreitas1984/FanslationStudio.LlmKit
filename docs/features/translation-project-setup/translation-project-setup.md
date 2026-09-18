# New translation project: quick guide

This guide is the short operating path for creating and running a new game translation project that consumes `FanslationStudio.LlmKit`. The detailed project layout is in [the downstream project structure guide](../../architecture/downstream-project-structure/downstream-project-structure.md); this page focuses on the order of work and which skill to use when something goes wrong.

## 1. Create the project

From the LlmKit repository, invoke the [`new-translation-project` skill](../../../.claude/skills/new-translation-project/SKILL.md). Answer its setup questions before it creates anything:

- game name and repository location
- game install directory
- BepInEx version
- IL2CPP or Mono, plus Unity version when relevant

The scaffold creates the downstream repository, `Files/` data layout, starter configuration, the `Translate/` and `Tests/` projects, the game plugin project, repository instructions, documentation, and synchronized skills. The downstream project references LlmKit through its project file; it is not a NuGet consumer.

Before translating, confirm the solution contains the required baseline:

- `Translate/` for reusable extraction, translation, packaging, and configuration code
- `Tests/` for xUnit tests and numbered pipeline/runbook steps
- `Files/` for `Raw/`, `Converted/`, `Mod/`, glossary, and configuration data
- `<GameName>Plugin/` for dumping, Harmony patches, and runtime injection

## 2. Wire the game-specific pieces

The scaffold is intentionally not a finished mod. In the downstream repo:

1. Implement the dumper that writes the game's supported text sources under `Files/Raw/Dumped/`.
2. Add each source file and its translatable columns to the tooling project's `TextFilesToSplit` configuration.
3. Add any game-specific placeholder patterns, glossary entries, validation/repair hooks, or QC exclusion rules in the downstream project.
4. Implement the plugin's runtime injection and, when needed, its dump-time patches.
5. Generate IL2CPP interop assemblies before building an IL2CPP plugin.
6. Keep game-specific behavior in the downstream repo; propose changes to LlmKit only when the behavior is genuinely shared across games.

Read the [translation pipeline feature docs](../translation-pipeline/) for extraction, splitting, glossary, validation, and quality-review behavior. Read the [packaging workflow guide](../packaging/packaging-workflows.md) before adding custom packaging logic.

## 3. Run a translation

Use the downstream repo's numbered pipeline steps in `Tests/` as the runbook. The exact class names vary by game, but the usual order is:

1. Dump or copy the game's source data into `Files/Raw/Dumped/`.
2. Export the configured files into split YAML under `Files/Raw/Export/`.
3. Run translation against the configured local model. The workflow validates placeholders, tags, formatting, and game-specific rules while retrying invalid results.
4. Inspect the generated `Files/Converted/*.yaml` files. Confirm each expected `TranslationSplit` has a translated value before packaging.
5. Run the optional quality-review pass when it is enabled in `Files/Config.yaml`. Review rejected, low-score, or flagged items according to the downstream project's triage process.
6. Package the accepted translations into `Files/Mod/`.
7. Build/copy the plugin and install the generated mod using the game's normal BepInEx layout.
8. Start the game and verify representative CSV, JSON, prefab, and dynamic-string paths in-game. Keep the raw input, converted YAML, packaged output, and game log for any failure you need to diagnose.

Do not wrap another retry loop around `TranslateSplitAsync`; LlmKit already owns translation retries and escalation. Prefer the downstream runbook and CI-safe tests for repeatable checks, and avoid treating a live LLM run as a unit-test result.

## 4. Diagnose problems with the right skill

Start with the symptom and invoke exactly one of these skills from the downstream repository:

| Symptom | Skill | First question it answers |
| --- | --- | --- |
| A specific line is still in the source language or is absent in-game | [`investigate-missing-translation`](../../../.claude/skills/investigate-missing-translation/SKILL.md) | Was it dumped, exported, translated, packaged, and injected? |
| `Files/Mod` has the wrong content, raw fallback, skipped columns, or reconstruction errors | [`investigate-packaging-issue`](../../../.claude/skills/investigate-packaging-issue/SKILL.md) | Which `TextFileType`, packaging workflow, or downstream post-processing owns the output? |
| QC scores, accepted corrections, freshness, or score gating look wrong | [`investigate-qc-issue`](../../../.claude/skills/investigate-qc-issue/SKILL.md) | Is QC enabled and fresh, and what do the workflow/helper/tests say should happen? |

For a missing translation, inspect the same line in this order: `Raw/Dumped` -> `Raw/Export` or `Converted` -> `Mod` -> runtime injection. For packaging, check `TextFileType`, `PackageOutput`, `SkipColumns`, and the downstream packaging entry point before changing shared code. For QC, check the downstream `qualityReview` config, then `QualityReviewWorkflow`, `QualityReviewHelpers`, and the focused tests.

If the investigation proves a generic LlmKit defect, record the settled behavior in this repository's `docs/`, not only in the downstream project's notes. Keep downstream-specific extraction, plugin, config, and game quirks in the downstream repository.

## Useful references

- [Downstream project structure](../../architecture/downstream-project-structure/downstream-project-structure.md)
- [Downstream test organization](../../architecture/downstream-project-structure/downstream-test-organization.md)
- [Config shape](../../architecture/downstream-project-structure/downstream-config-shape.md)
- [Translation pipeline](../translation-pipeline/)
- [Packaging workflows](../packaging/packaging-workflows.md)
- [Quality review pass](../translation-pipeline/quality-review-pass.md)
