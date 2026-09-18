# FanslationStudio.LlmKit — Documentation Hub

Canonical navigation entry point for this repository — so a human or an AI agent (Copilot, Claude
Code, or otherwise) can find the right source of truth without relying on vendor-specific memory.

## What this repo is

A reusable "over LLM" game-translation kit: shared `Line → Splits → (Templates)` data model,
CSV/compound-field parsing, LLM translation service (retry/escalation/validation), and workflow
classes for extracting translatable text from game data and reassembling translated output back
into the original file shape. Consumed by downstream "over LLM" translation projects via a
**project reference** (`../../FanslationStudio.LlmKit/...csproj`), not a NuGet package — it's meant
to be edited in lockstep with the games that use it.

## Related downstream repos

All of these are per-game translation projects that consume this repo via project reference. Each
should have its own `AGENTS.md`/`docs/README.md` mirroring this repo's taxonomy, and should
cross-link back to this repo's QC/Packaging docs rather than re-explaining LlmKit-internal
behavior locally.

| Repo | Notes |
| --- | --- |
| `../../DragonHierOverLlm` | Most actively developed; source of most cross-repo postmortems referenced below. |
| `../../LegendOfMortalOverLlm` | |
| `../../WanXiangOverLlm` | |

## Documentation taxonomy

Every repository uses one root `docs/` folder for all documentation:

1. **`docs/plans/`** — plans for upcoming or completed work, including design history and proposed
  changes.
2. **`docs/investigations/`** — investigations, incident analysis, bug-fix postmortems, and other
  diagnostic writeups.
3. **`docs/features/<feature-or-project>/`** — current-state documentation for a feature or large
  project, grouped into a feature-specific folder when the feature has more than one document.

The agent-facing entry points remain outside `docs/`; the issue index lives at `docs/KNOWN_ISSUES.md`:

4. **Scoped instructions** (`.github/copilot-instructions.md`, `AGENTS.md`) — auto-injected into
   agent context on every edit (`applyTo: "**"`, this repo has no sub-projects to scope by path).
   Kept short: current-state rules, safety invariants ("the golden rule"), extension points. Never
   contains investigation narratives or bug-fix postmortems.
5. **`docs/KNOWN_ISSUES.md`** — an issue index only, not auto-loaded. One line per known issue or
  postmortem, linking to the full investigation document.

## Source-of-truth rules

- `.github/copilot-instructions.md` (and its vendor-neutral mirror `AGENTS.md`) is the
  current-state source of truth for rules and invariants. If a document under `docs/` disagrees
  with the instructions file, the instructions file wins for current behavior — the topic file may
  still hold accurate historical narrative.
- `docs/KNOWN_ISSUES.md` is an index only; never treat it as the full explanation of an issue.
- Do not update instructions files, `docs/KNOWN_ISSUES.md`, or `docs/` topic files during every
  exploratory edit or intermediate fix attempt. Inspect existing docs first, then consolidate one
  documentation update when a substantial task is complete and its behavior is settled. Update
  instructions only when a current-state rule changes, and update indexes when links or durable topic
  coverage require it.
- **Downstream repos record LlmKit-internal findings here, not in their own repo notes** — if
  you're reverse-engineering how something in this library works while sitting in a consuming
  repo's session (e.g. `DragonHierOverLlm`), the finding belongs in this repo's `docs/`, since this
  is the sibling repo the logic actually belongs to.

## Docs index

The structure references below are for **downstream game-translation repositories**, not for the
`FanslationStudio.LlmKit` repository itself. For this kit's own architecture and repository rules,
start with [`architecture/ARCHITECTURE.md`](architecture/ARCHITECTURE.md), [`AGENTS.md`](../AGENTS.md),
and [`.github/copilot-instructions.md`](../.github/copilot-instructions.md).

| Topic | File |
| --- | --- |
| Create and run a new downstream translation project | [`features/translation-project-setup/translation-project-setup.md`](features/translation-project-setup/translation-project-setup.md) |
| Full current-state architecture reference | [`architecture/ARCHITECTURE.md`](architecture/ARCHITECTURE.md) |
| `CompoundFieldSplitter` feature behavior | [`features/compound-field-splitting/compound-field-splitting.md`](features/compound-field-splitting/compound-field-splitting.md) |
| `TranslationService` retry/escalation mechanics and postmortems | [`investigations/translation-retry-escalation-and-fixes.md`](investigations/translation-retry-escalation-and-fixes.md) |
| Text handling: CSV/JSON, PrefabText, dynamic strings, and LocalTextString | [`features/text-handling/csv-json-workflows.md`](features/text-handling/csv-json-workflows.md), [`features/text-handling/`](features/text-handling/) |
| Translation pipeline: glossary, workflows, GameHooks, and quality review | [`features/translation-pipeline/`](features/translation-pipeline/) |
| Post-translation quality review pass — current behavior and local postmortems | [`features/translation-pipeline/quality-review-pass.md`](features/translation-pipeline/quality-review-pass.md), [`investigations/quality-review-postmortems.md`](investigations/quality-review-postmortems.md) |
| Packaging workflows and file types | [`features/packaging/packaging-workflows.md`](features/packaging/packaging-workflows.md) |
| Downstream translation-project structure and sub-project layout | [`architecture/downstream-project-structure/downstream-project-structure.md`](architecture/downstream-project-structure/downstream-project-structure.md) |
| Downstream translation-project test organization | [`architecture/downstream-project-structure/downstream-test-organization.md`](architecture/downstream-project-structure/downstream-test-organization.md) |
| Downstream translation-project `Config.yaml` shape | [`architecture/downstream-project-structure/downstream-config-shape.md`](architecture/downstream-project-structure/downstream-config-shape.md) |
| Downstream BepInEx plugin project layout | [`architecture/downstream-project-structure/downstream-plugin-project-layout.md`](architecture/downstream-project-structure/downstream-plugin-project-layout.md) |
| Downstream repository docs taxonomy | [`architecture/downstream-project-structure/downstream-repository-docs-taxonomy.md`](architecture/downstream-project-structure/downstream-repository-docs-taxonomy.md) |
| Architectural decisions | [`architecture/decisions/`](architecture/decisions/) |

See [`KNOWN_ISSUES.md`](KNOWN_ISSUES.md) for the issue and postmortem index. It is intentionally
separate from this navigation table and contains one-line pointers only.

## Where should I look?

| Task | Start here |
| --- | --- |
| Understand the core `Line`/`Split`/`Template` data model or config loading | [`.github/copilot-instructions.md`](../.github/copilot-instructions.md), [`architecture/ARCHITECTURE.md`](architecture/ARCHITECTURE.md) |
| Debug/extend `CompoundFieldSplitter.Decompose`/`Reconstruct` | [`.github/copilot-instructions.md`](../.github/copilot-instructions.md) (current-state rules), [`features/compound-field-splitting/compound-field-splitting.md`](features/compound-field-splitting/compound-field-splitting.md) |
| Investigate a translation retry/validation/escalation issue | [`investigations/translation-retry-escalation-and-fixes.md`](investigations/translation-retry-escalation-and-fixes.md) |
| Add or integrate prefab, dynamic, or local-text strings | [`features/text-handling/`](features/text-handling/), [`features/text-handling/dynamic-strings.md`](features/text-handling/dynamic-strings.md), [`features/text-handling/local-text-string.md`](features/text-handling/local-text-string.md) |
| Configure terminology, workflows, hooks, or quality review | [`features/translation-pipeline/`](features/translation-pipeline/) |
| Create a new translation project or run the translation workflow | [`features/translation-project-setup/translation-project-setup.md`](features/translation-project-setup/translation-project-setup.md) |
| Investigate a packaging issue | [`features/packaging/packaging-workflows.md`](features/packaging/packaging-workflows.md) |
| See how a downstream game project consumes this library | Check the consuming repository's own `AGENTS.md` and `docs/README.md`; this repo documents the shared library contract. |
| Check/reconcile a downstream translation project's structure | [`architecture/downstream-project-structure/downstream-project-structure.md`](architecture/downstream-project-structure/downstream-project-structure.md) (and its sibling topic docs) |
