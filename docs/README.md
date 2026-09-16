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

Same three-tier split used by downstream consuming repos:

1. **Scoped instructions** (`.github/copilot-instructions.md`, `AGENTS.md`) — auto-injected into
   agent context on every edit (`applyTo: "**"`, this repo has no sub-projects to scope by path).
   Kept short: current-state rules, safety invariants ("the golden rule"), extension points. Never
   contains investigation narratives or bug-fix postmortems.
2. **`KNOWN_ISSUES.md`** (repo root) — an index only, not auto-loaded. One line per known
   issue/investigation/design-history writeup, linking to the full document.
3. **`docs/*.md`** — one topic file per investigation, bug fix, or reference document. Read only
   the specific file relevant to the current task, not the whole folder.

## Source-of-truth rules

- `.github/copilot-instructions.md` (and its vendor-neutral mirror `AGENTS.md`) is the
  current-state source of truth for rules and invariants. If a `docs/*.md` topic file disagrees
  with the instructions file, the instructions file wins for current behavior — the topic file may
  still hold accurate historical narrative.
- `KNOWN_ISSUES.md` is an index only; never treat it as the full explanation of an issue.
- Do not update instructions files, `KNOWN_ISSUES.md`, or `docs/` topic files as a side effect of a
  fix or feature. Only write documentation when explicitly asked to.
- **Downstream repos record LlmKit-internal findings here, not in their own repo notes** — if
  you're reverse-engineering how something in this library works while sitting in a consuming
  repo's session (e.g. `DragonHierOverLlm`), the finding belongs in this repo's `docs/`, since this
  is the sibling repo the logic actually belongs to.

## Docs index

| Topic | File |
| --- | --- |
| Full current-state architecture reference (data model, config, workflow entry points, translation pipeline, validation) | [`ARCHITECTURE.md`](ARCHITECTURE.md) |
| Proposed/in-progress performance work (scheduler comparison, pooled vs. batched) | [`OPTIMIZATION_PLAN.md`](OPTIMIZATION_PLAN.md) |
| `CompoundFieldSplitter` regex design history and rationale (why each character class/absorption rule exists) | [`compoundfieldsplitter-design.md`](compoundfieldsplitter-design.md) |
| `TranslationService` retry/escalation mechanics + real-run bug-fix postmortems (correction-suffix leak, game-specific-hook ordering bug, leading punctuation handling) | [`translation-retry-escalation-and-fixes.md`](translation-retry-escalation-and-fixes.md) |
| `TextFileType.PrefabText` workflow design (flat, row/column-less dumped text files) | [`prefabtext-workflow.md`](prefabtext-workflow.md) |
| Post-translation quality review pass — current-state architecture (data model, `QualityReviewWorkflow`, staleness/freshness, packaging, presets/prompts) | [`quality-review-pass-architecture.md`](quality-review-pass-architecture.md) |
| Post-translation quality review pass — original design plan/history (spans this repo + `DragonHierOverLlm`) | [`../../DragonHierOverLlm/docs/plans/quality-review-pass.md`](../../DragonHierOverLlm/docs/plans/quality-review-pass.md) |
| Packaging — current-state architecture (Csv/Json/PrefabText/DynamicString reconstruction, QC score-gating, raw-fallback rules, per-workflow differences) | [`packaging-reference.md`](packaging-reference.md) |
| Canonical downstream repo shape — index doc + sub-project layout pattern (deliberately-maintained target for `new-translation-project`/`upgrade-translation-project` to diff against, not whatever `DragonHierOverLlm` currently looks like) | [`canonical-project-shape.md`](canonical-project-shape.md) |
| Canonical test file organization — one-file-per-workflow vs. shared files, and the `"0"`–`"9"` `DisplayName` numbering convention | [`canonical-test-organization.md`](canonical-test-organization.md) |
| Canonical `Config.yaml` shape — required fields vs. game-specific placeholders | [`canonical-config-shape.md`](canonical-config-shape.md) |
| Canonical BepInEx plugin project layout — IL2CPP and Mono branches | [`canonical-plugin-project-layout.md`](canonical-plugin-project-layout.md) |
| Canonical root-level docs taxonomy a downstream repo should have (`AGENTS.md`/`CLAUDE.md`/`docs/README.md`/per-sub-project `KNOWN_ISSUES.md`) | [`canonical-repo-docs-taxonomy.md`](canonical-repo-docs-taxonomy.md) |

See [`KNOWN_ISSUES.md`](../KNOWN_ISSUES.md) for the full issue index (mirrors this table with
one-line summaries).

## Where should I look?

| Task | Start here |
| --- | --- |
| Understand the core `Line`/`Split`/`Template` data model or config loading | [`.github/copilot-instructions.md`](../.github/copilot-instructions.md), [`ARCHITECTURE.md`](ARCHITECTURE.md) |
| Debug/extend `CompoundFieldSplitter.Decompose`/`Reconstruct` | [`.github/copilot-instructions.md`](../.github/copilot-instructions.md) (current-state rules), [`compoundfieldsplitter-design.md`](compoundfieldsplitter-design.md) (why) |
| Investigate a translation retry/validation/escalation issue | [`translation-retry-escalation-and-fixes.md`](translation-retry-escalation-and-fixes.md) |
| Add support for a new flat/prefab-style dumped text file | [`prefabtext-workflow.md`](prefabtext-workflow.md) |
| Understand or extend the quality-review pass work | [`quality-review-pass-architecture.md`](quality-review-pass-architecture.md) (current-state), [`../../DragonHierOverLlm/docs/plans/quality-review-pass.md`](../../DragonHierOverLlm/docs/plans/quality-review-pass.md) (design history) |
| Investigate a packaging issue | [`packaging-reference.md`](packaging-reference.md) |
| See how a downstream game project consumes this library | `DragonHierOverLlm/.github/instructions/tests-translation-workflow.instructions.md` (sibling repo) |
| Check/reconcile a downstream repo's structure against the canonical shape | [`canonical-project-shape.md`](canonical-project-shape.md) (and its `canonical-test-organization.md`/`canonical-config-shape.md`/`canonical-plugin-project-layout.md`/`canonical-repo-docs-taxonomy.md` siblings) |
