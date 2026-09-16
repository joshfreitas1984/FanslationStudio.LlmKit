# Canonical root-level docs taxonomy for a downstream repo

Part of the [canonical downstream project shape](canonical-project-shape.md). This mirrors the
three-tier documentation taxonomy already defined as source of truth in
`FanslationStudio.LlmKit/docs/README.md`'s own "Documentation taxonomy" section — this doc
describes the **file/folder shape** a downstream repo should have to implement that taxonomy, it
does not restate or override the rules themselves (LlmKit's `docs/README.md` wins if the two ever
disagree on the rules).

Source of truth for the shape: `DragonHierOverLlm`'s root `AGENTS.md`, `CLAUDE.md`, `docs/README.md`,
and its per-sub-project `KNOWN_ISSUES.md`/`.github/instructions/*.instructions.md` files, as of
2026-09-16.

## Expected file list at repo root

| File | Purpose | DragonHeir shape observed |
| --- | --- | --- |
| `AGENTS.md` | Vendor-neutral universal repo rules — the canonical rules file every other vendor-specific file (Copilot's `.github/copilot-instructions.md`, `CLAUDE.md`) points back to. Short and operational: lists sub-projects, repository-wide "don't do X" rules, and a pointer to `docs/README.md`'s "Where should I look?" table. Never contains investigation narrative. | `AGENTS.md` present at root; points to `docs/README.md` and to each sub-project's own scoped instructions file. |
| `CLAUDE.md` | Thin pointer only — states it intentionally does not duplicate `AGENTS.md`/`docs/README.md`'s content. | DragonHeir's `CLAUDE.md` is 6 lines, purely a pointer (mirrors this exact repo's own `CLAUDE.md` pattern). |
| `.github/copilot-instructions.md` | Copilot-specific top-level entry point; same content intent as `AGENTS.md` but in the format Copilot auto-loads. Restated (not just linked) in `AGENTS.md` for non-Copilot agents. | Present, described in `docs/README.md` as "the top-level entry point that links to all of the above and states the repository-wide workflow rules." |
| `.github/instructions/<subproject>.instructions.md` | Per-sub-project scoped instructions, auto-injected only when editing files under that sub-project's path (`applyTo` scoping). Current-state rules and safety invariants for that sub-project only — e.g. IL2CPP interop safety rules for the plugin project. | One per sub-project that has enough current-state rules to warrant it (DragonHeir: `converter.instructions.md`, `dragonheirplugin.instructions.md`, `tests-translation-workflow.instructions.md`). Not every sub-project needs one — DragonHeir's `Verify/` and `Files/` have none, since they're a plain harness and a data folder respectively. |
| `docs/README.md` | The canonical documentation hub — repo overview (sub-project purpose table), the documentation taxonomy itself, source-of-truth rules, a table mapping each sub-project to its instructions file + issue index, and a task-oriented "Where should I look?" table. | Present; this is the file this whole taxonomy doc mirrors the shape of. |
| `<subproject>/KNOWN_ISSUES.md` | Per-sub-project issue **index only** — one line per known issue/investigation, linking out to the full `docs/*.md` writeup. Never the full explanation itself. | One per sub-project that has accumulated issues worth indexing (DragonHeir: `Converter/KNOWN_ISSUES.md`, `DragonHeirPlugin/KNOWN_ISSUES.md`, `Tests/KNOWN_ISSUES.md`). DragonHeir has **no single root-level `KNOWN_ISSUES.md`** — the index is per-sub-project, unlike this repo (`FanslationStudio.LlmKit`) which keeps one root-level `KNOWN_ISSUES.md` for the whole (single-project) repo. A new multi-sub-project downstream repo should follow DragonHeir's per-sub-project pattern, not put one at the repo root. |
| `<subproject>/docs/*.md` | One topic file per investigation, bug fix, or reference doc, scoped to that sub-project. Read only the file relevant to the current task. | e.g. `Tests/docs/gamefilehandling-reference.md`, `Tests/docs/qc-qualityscore-noise-investigation.md`. |
| `<subproject>/README.md` | Project-level how-to-run/build doc, explicitly separate from the bug-history documentation above (`docs/README.md` calls this out directly: "Project-level `README.md` files ... describe how to run/build that project, separate from the bug-history documentation above"). | e.g. `Converter/README.md`. |
| root `readme.md` | End-user-facing install/play instructions for the released patch — distinct from any of the agent-facing docs above. | DragonHeir's `docs/README.md` "Where should I look?" table points installers at `../readme.md`. |

## Taxonomy tiers, restated as shape (not rules)

1. **Tier 1 — auto-loaded, scoped instructions.** Root `AGENTS.md`/`.github/copilot-instructions.md`
   (repo-wide) plus per-sub-project `.github/instructions/*.instructions.md` (path-scoped). Kept
   short; no investigation narrative.
2. **Tier 2 — `KNOWN_ISSUES.md`, per sub-project, index only.** Not auto-loaded.
3. **Tier 3 — `docs/*.md`, per sub-project, one topic file per investigation/reference.**

A new or upgraded downstream repo should have all three tiers represented, with tier 2 and 3 files
scoped **per sub-project** once the repo has more than one independent sub-project (this is where
DragonHeir differs structurally from `FanslationStudio.LlmKit` itself, which is a single project
and so keeps one root-level `KNOWN_ISSUES.md`/`docs/` instead).

## Cross-linking rule

Per this repo's own `docs/README.md`, LlmKit-internal mechanics (the `QualityReviewWorkflow`
implementation, packaging logic, retry/escalation mechanics) are documented **only** in
`FanslationStudio.LlmKit/docs/`, and a downstream repo's docs should cross-link to those rather than
re-explaining LlmKit-internal behavior locally. DragonHeir's `docs/README.md` demonstrates this
directly in its "Where should I look?" row for the QC pass, pointing to
`../FanslationStudio.LlmKit/docs/quality-review-pass-architecture.md` and labeling it "sibling
repo — the actual `QualityReviewWorkflow` implementation lives there, not in this repo."
