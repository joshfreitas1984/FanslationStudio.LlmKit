# Canonical downstream project shape

This is the deliberately-maintained reference for what a downstream "OverLlm" translation repo
(`DragonHierOverLlm`, `WanXiangOverLlm`, `LegendOfMortalOverLlm`, or a new one) should look like. It
exists so the `new-translation-project` skill and the planned `upgrade-translation-project` skill
diff against a documented target, not against whatever `DragonHierOverLlm` currently happens to
contain — DragonHeir is allowed to carry WIP/experimental structure without that silently becoming
"the standard," and a scaffold/upgrade run doesn't need access to the DragonHeir repo at all.

**Source of truth for this doc's content:** `DragonHierOverLlm` as of 2026-09-16, cross-checked
against `WanXiangOverLlm` and `LegendOfMortalOverLlm` for naming variance. When a downstream repo's
structure and this doc disagree, treat the disagreement as drift to reconcile (in either
direction — this doc can be wrong too) rather than silently trusting either side.

This is an index. The detailed shape lives in four sibling docs, one per researched area:

| Area | Doc |
| --- | --- |
| Test file organization, naming, and numbering conventions | [`canonical-test-organization.md`](canonical-test-organization.md) |
| `Config.yaml` structure | [`canonical-config-shape.md`](canonical-config-shape.md) |
| BepInEx plugin project layout (IL2CPP vs. Mono) | [`canonical-plugin-project-layout.md`](canonical-plugin-project-layout.md) |
| Root-level docs taxonomy (`AGENTS.md`, `CLAUDE.md`, `docs/README.md`, `KNOWN_ISSUES.md`) | [`canonical-repo-docs-taxonomy.md`](canonical-repo-docs-taxonomy.md) |

## Sub-project layout pattern

Every downstream repo is a small multi-project solution of **independent sub-projects**, not one
monolithic project. Names vary per repo (this is expected — a repo names its plugin project after
the game, e.g. `DragonHeirPlugin`, `EnglishPatch`, `Plugin`), but the same five structural roles
recur. Observed names, side by side:

| Role | DragonHierOverLlm | WanXiangOverLlm | LegendOfMortalOverLlm |
| --- | --- | --- | --- |
| Tooling/translation-workflow project (drives extraction + LLM translation + packaging; also hosts the xunit test-as-runbook facts) | `Tests/` | `Translate/` + `Tests/` (split) | `Translate/` + `Tests/` (split) |
| BepInEx runtime plugin (Harmony patches, IL2CPP interop) | `DragonHeirPlugin/` | `EnglishPatch/` | `Plugin/` |
| Shared assembly (types referenced by both the tooling project and the runtime plugin, e.g. shared data-shape code) | — (not present; DragonHeir has no separate shared-assembly project) | `SharedAssembly/` | `SharedAssembly/` |
| Working-directory data project (Raw/Converted/Mod/Glossary layout, browsable in the IDE) | `Files/` | `Files/` | `Files/` |
| Decompiler/reverse-engineering support project (IL2CPP only) | `Converter/` (Ghidra-based `Assembly-CSharp.dll`/`GameAssembly.dll` decompile) | — | — |
| Reproduction harness for isolating logic bugs outside the running game | `Verify/` | — (not observed) | — (not observed) |
| Raw decompiled/dumped text-asset scratch data | `TextAsset/` | — | — |

Notes on what's canonical vs. per-repo:

- The **structural roles** (a tooling project, a plugin project, a Files data project, a Tests
  project) are the canonical part. A new/upgraded repo should have all of these in some form.
- **Whether Translate and Tests are one project or two is not settled by DragonHeir alone.**
  DragonHeir combines them into a single `Tests/` project (the xunit facts directly call
  `TranslationWorkflow`/`QualityReviewWorkflow` etc. and also serve as the "run a step of the
  pipeline" entry points). WanXiang and LegendOfMortal split this into a `Translate/` library project
  plus a separate `Tests/` project referencing it. Since DragonHeir is this doc's source of truth,
  **the single-`Tests/`-project shape is the canonical default** for a new repo scaffolded from
  scratch; the two-project split is a documented, acceptable variant (see
  `canonical-test-organization.md`) — don't treat a repo using the split as drift on that basis
  alone.
- `SharedAssembly/` is only needed when the plugin and the tooling project must share compiled
  types across the IL2CPP/.NET boundary. DragonHeir doesn't have one; that's a legitimate
  simplification for a repo whose plugin doesn't need to share types with its tooling project, not
  evidence the role is optional in general — add one if the same need arises.
- `Converter/` and `Verify/` are DragonHeir-specific: `Converter/` exists because DragonHeir's game
  is IL2CPP and needed Ghidra-based decompilation to reverse-engineer data structures; `Verify/` is
  a general-purpose reproduction harness DragonHeir happened to formalize first. Both are good
  patterns to offer to a new repo (especially `Verify/` — see `AGENTS.md`'s "do not create
  throwaway verification harness projects" rule, which assumes `Verify/` exists), but neither is
  mandatory scaffolding for a repo that doesn't need IL2CPP decompilation.

## Not yet covered here

This doc (and its four siblings) covers structure — file/folder layout, naming, and config shape.
It does not cover LlmKit-internal mechanics (workflow classes, data model, retry/escalation) — that
lives in this repo's `ARCHITECTURE.md`, `quality-review-pass-architecture.md`, and
`packaging-reference.md`, per `docs/README.md`'s taxonomy.
