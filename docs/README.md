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
- Do not update instructions files, `docs/KNOWN_ISSUES.md`, or `docs/` topic files as a side effect of a
  fix or feature. Only write documentation when explicitly asked to.
- **Downstream repos record LlmKit-internal findings here, not in their own repo notes** — if
  you're reverse-engineering how something in this library works while sitting in a consuming
  repo's session (e.g. `DragonHierOverLlm`), the finding belongs in this repo's `docs/`, since this
  is the sibling repo the logic actually belongs to.

## Docs index

| Topic | File |
| --- | --- |
| Full current-state architecture reference | [`architecture/ARCHITECTURE.md`](architecture/ARCHITECTURE.md) |
| `CompoundFieldSplitter` feature behavior | [`features/compound-field-splitting/compound-field-splitting.md`](features/compound-field-splitting/compound-field-splitting.md) |
| `TranslationService` retry/escalation mechanics and postmortems | [`investigations/translation-retry-escalation-and-fixes.md`](investigations/translation-retry-escalation-and-fixes.md) |
| Text handling: CSV/JSON, PrefabText, dynamic strings, and LocalTextString | [`features/text-handling/csv-json-workflows.md`](features/text-handling/csv-json-workflows.md), [`features/text-handling/`](features/text-handling/) |
| Translation pipeline: glossary, workflows, GameHooks, and quality review | [`features/translation-pipeline/`](features/translation-pipeline/) |
| Post-translation quality review pass — original design plan/history (spans this repo + `DragonHierOverLlm`) | [`../../DragonHierOverLlm/docs/plans/quality-review-pass.md`](../../DragonHierOverLlm/docs/plans/quality-review-pass.md) |
| Packaging workflows and file types | [`features/packaging/packaging-workflows.md`](features/packaging/packaging-workflows.md) |
| Canonical downstream repo shape and sub-project layout | [`architecture/repo-structure/canonical-project-shape.md`](architecture/repo-structure/canonical-project-shape.md) |
| Canonical test file organization | [`architecture/repo-structure/canonical-test-organization.md`](architecture/repo-structure/canonical-test-organization.md) |
| Canonical `Config.yaml` shape | [`architecture/repo-structure/canonical-config-shape.md`](architecture/repo-structure/canonical-config-shape.md) |
| Canonical BepInEx plugin project layout | [`architecture/repo-structure/canonical-plugin-project-layout.md`](architecture/repo-structure/canonical-plugin-project-layout.md) |
| Canonical root-level docs taxonomy for downstream repos | [`architecture/repo-structure/canonical-repo-docs-taxonomy.md`](architecture/repo-structure/canonical-repo-docs-taxonomy.md) |
| Architectural decisions | [`architecture/decisions/`](architecture/decisions/) |

See [`KNOWN_ISSUES.md`](KNOWN_ISSUES.md) for the full issue index (mirrors this table with
one-line summaries).

## Where should I look?

| Task | Start here |
| --- | --- |
| Understand the core `Line`/`Split`/`Template` data model or config loading | [`.github/copilot-instructions.md`](../.github/copilot-instructions.md), [`architecture/ARCHITECTURE.md`](architecture/ARCHITECTURE.md) |
| Debug/extend `CompoundFieldSplitter.Decompose`/`Reconstruct` | [`.github/copilot-instructions.md`](../.github/copilot-instructions.md) (current-state rules), [`features/compound-field-splitting/compound-field-splitting.md`](features/compound-field-splitting/compound-field-splitting.md) |
| Investigate a translation retry/validation/escalation issue | [`investigations/translation-retry-escalation-and-fixes.md`](investigations/translation-retry-escalation-and-fixes.md) |
| Add or integrate prefab, dynamic, or local-text strings | [`features/text-handling/`](features/text-handling/) |
| Configure terminology, workflows, hooks, or quality review | [`features/translation-pipeline/`](features/translation-pipeline/) |
| Investigate a packaging issue | [`features/packaging/packaging-workflows.md`](features/packaging/packaging-workflows.md) |
| See how a downstream game project consumes this library | `DragonHierOverLlm/.github/instructions/tests-translation-workflow.instructions.md` (sibling repo) |
| Check/reconcile a downstream repo's structure against the canonical shape | [`architecture/repo-structure/canonical-project-shape.md`](architecture/repo-structure/canonical-project-shape.md) (and its sibling docs) |
