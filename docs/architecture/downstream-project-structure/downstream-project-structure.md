# Canonical downstream translation-project shape

> **Scope:** This document describes the repository layout for a new game-specific translation
> project that consumes `FanslationStudio.LlmKit`. It does not describe the structure of the
> `FanslationStudio.LlmKit` repository itself; for that, use [`../ARCHITECTURE.md`](../ARCHITECTURE.md)
> and the root [`AGENTS.md`](../../../AGENTS.md).

This is the deliberately-maintained reference for what a downstream "OverLlm" translation repo
(`DragonHierOverLlm`, `WanXiangOverLlm`, `LegendOfMortalOverLlm`, or a new one) should look like. It
is the migration target for existing repos and the only layout the `new-translation-project` skill
should create. Existing repositories may still contain legacy variations; those are migration
inputs, not alternative standards.

**Source of truth for this target:** the shared LlmKit/downstream-project contract, informed by the
existing projects as of 2026-09-16. When an existing downstream repo disagrees with this document,
treat it as structure drift to migrate rather than as a reason to add another supported variant.

This is an index. The detailed shape lives in four sibling docs, one per researched area:

| Area | Doc |
| --- | --- |
| Test file organization, naming, and numbering conventions | [`downstream-test-organization.md`](downstream-test-organization.md) |
| `Config.yaml` structure | [`downstream-config-shape.md`](downstream-config-shape.md) |
| BepInEx plugin project layout (IL2CPP vs. Mono) | [`downstream-plugin-project-layout.md`](downstream-plugin-project-layout.md) |
| Root-level docs taxonomy (`AGENTS.md`, `CLAUDE.md`, `docs/README.md`, `docs/KNOWN_ISSUES.md`) | [`downstream-repository-docs-taxonomy.md`](downstream-repository-docs-taxonomy.md) |

## Required project layout

Every downstream repo uses these four required projects with these ownership boundaries:

| Path | Responsibility |
| --- | --- |
| `Translate/` | Class library containing game-specific extraction, translation, packaging configuration, and reusable workflow helpers. References `FanslationStudio.LlmKit`. |
| `Tests/` | xUnit project containing numbered pipeline/runbook steps and regression tests. References `Translate/`; it owns execution of the workflow, not the reusable workflow implementation. |
| `Files/` | Browsable working-directory project containing `Raw/`, `Converted/`, `Mod/`, `Glossary/`, configuration, and translation data. |
| `<GameName>Plugin/` | BepInEx runtime plugin containing dumping, Harmony patches, and runtime injection. The exact game name is part of the project and assembly name. |

The solution file at the repository root includes all four projects. `Translate/` and `Tests/`
must not be collapsed into one project for a new repo. A refactor of an existing combined project
moves reusable workflow/configuration classes into `Translate/` and leaves numbered facts and tests
in `Tests/`.

## Optional projects

Add these only when the game requires them; they are not part of the baseline:

- `SharedAssembly/` — shared types needed across the tooling/runtime boundary.
- `Converter/` — IL2CPP decompilation or reverse-engineering support.
- `Verify/` — a persistent reproduction harness for logic that should be isolated from the live game.

## Migration from existing projects

Known legacy layouts are migration inputs, not supported alternatives:

- A combined DragonHeir-style `Tests/` project splits into `Translate/` plus `Tests/`.
- `EnglishPatch/`, `Plugin/`, or another game-specific plugin directory is renamed to
  `<GameName>Plugin/`.
- Existing `SharedAssembly/`, `Converter/`, `Verify/`, and game data folders are retained only when
  they satisfy the optional-project definitions above.

## Not yet covered here

This doc (and its four siblings) covers structure — file/folder layout, naming, and config shape.
It does not cover LlmKit-internal mechanics (workflow classes, data model, retry/escalation) — that
lives in this repo's `architecture/ARCHITECTURE.md`,
`features/translation-pipeline/quality-review-pass.md`, and
`features/packaging/packaging-workflows.md`, per `docs/README.md`'s taxonomy.
