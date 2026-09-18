# Downstream repository documentation taxonomy

Part of the [downstream translation-project structure](downstream-project-structure.md). This mirrors the
documentation taxonomy defined in `FanslationStudio.LlmKit/docs/README.md`. Every child repo has
exactly one root `docs/` folder, with `plans`, `investigations`, and `features` beneath it. This
doc describes the **file/folder shape** a downstream repo should use; the LlmKit documentation hub
remains the source of truth if the two ever disagree.

Source of truth for the shape: `DragonHierOverLlm`'s root `AGENTS.md`, `CLAUDE.md`, `docs/README.md`,
and its `docs/KNOWN_ISSUES.md`/`.github/instructions/*.instructions.md` files, as of
2026-09-16.

## Expected file list at repo root

| File | Purpose | DragonHeir shape observed |
| --- | --- | --- |
| `AGENTS.md` | Vendor-neutral universal repo rules — the canonical rules file every other vendor-specific file (Copilot's `.github/copilot-instructions.md`, `CLAUDE.md`) points back to. Short and operational: lists sub-projects, repository-wide "don't do X" rules, and a pointer to `docs/README.md`'s "Where should I look?" table. Never contains investigation narrative. | `AGENTS.md` present at root; points to `docs/README.md` and to each sub-project's own scoped instructions file. |
| `CLAUDE.md` | Thin pointer only — states it intentionally does not duplicate `AGENTS.md`/`docs/README.md`'s content. | DragonHeir's `CLAUDE.md` is 6 lines, purely a pointer (mirrors this exact repo's own `CLAUDE.md` pattern). |
| `.github/copilot-instructions.md` | Copilot-specific top-level entry point; same content intent as `AGENTS.md` but in the format Copilot auto-loads. Restated (not just linked) in `AGENTS.md` for non-Copilot agents. | Present, described in `docs/README.md` as "the top-level entry point that links to all of the above and states the repository-wide workflow rules." |
| `.github/instructions/<subproject>.instructions.md` | Per-sub-project scoped instructions, auto-injected only when editing files under that sub-project's path (`applyTo` scoping). Current-state rules and safety invariants for that sub-project only — e.g. IL2CPP interop safety rules for the plugin project. | One per sub-project that has enough current-state rules to warrant it (DragonHeir: `converter.instructions.md`, `dragonheirplugin.instructions.md`, `tests-translation-workflow.instructions.md`). Not every sub-project needs one — DragonHeir's `Verify/` and `Files/` have none, since they're a plain harness and a data folder respectively. |
| `docs/README.md` | The canonical documentation hub — repo overview (sub-project purpose table), the documentation taxonomy itself, source-of-truth rules, a table mapping each sub-project to its instructions file + issue index, and a task-oriented "Where should I look?" table. | Present; this is the file this whole taxonomy doc mirrors the shape of. |
| `docs/README.md` | Documentation hub and navigation entry point. | Present at the root of the single docs tree. |
| `docs/KNOWN_ISSUES.md` | Issue **index only** — one line per known issue or postmortem, linking to the full investigation document under `docs/investigations/`. Never the full explanation itself. | Present in the root docs tree. |
| `docs/plans/` | Plans for upcoming or completed work, including design history and proposed changes. | Present when the repo has plan documents. |
| `docs/investigations/` | Investigations, incident analysis, bug-fix postmortems, and diagnostic writeups. | Present when the repo has investigation documents. |
| `docs/features/<feature-or-project>/` | Current-state documentation for a feature or large project, grouped by feature/project. | Present for each feature with documentation. |
| `docs/architecture/` | Architecture references and the `decisions/` ADR collection. | Present for shared architecture and structure guidance. |
| `<subproject>/README.md` | Project-level how-to-run/build doc, explicitly separate from the bug-history documentation above (`docs/README.md` calls this out directly: "Project-level `README.md` files ... describe how to run/build that project, separate from the bug-history documentation above"). | e.g. `Converter/README.md`. |
| root `readme.md` | End-user-facing install/play instructions for the released patch — distinct from any of the agent-facing docs above. | DragonHeir's `docs/README.md` "Where should I look?" table points installers at `../readme.md`. |

## Taxonomy shape

1. **Tier 1 — auto-loaded, scoped instructions.** Root `AGENTS.md`/`.github/copilot-instructions.md`
   (repo-wide) plus per-sub-project `.github/instructions/*.instructions.md` (path-scoped). Kept
   short; no investigation narrative.
2. **`docs/KNOWN_ISSUES.md`, index only.** Not auto-loaded.
3. **One root `docs/` folder**, containing `README.md`, `KNOWN_ISSUES.md`, `plans/`, `investigations/`, and
   `features/`.

Sub-project `README.md` files remain local how-to-run/build documentation. They must not introduce
additional `docs/` folders; all repo documentation belongs in the single root docs tree.

## Cross-linking rule

Per this repo's own `docs/README.md`, LlmKit-internal mechanics (the `QualityReviewWorkflow`
implementation, packaging logic, retry/escalation mechanics) are documented **only** in
`FanslationStudio.LlmKit/docs/`, and a downstream repo's docs should cross-link to those rather than
re-explaining LlmKit-internal behavior locally. A child repo's `docs/README.md` should demonstrate this
directly in its "Where should I look?" row for the QC pass, pointing to
`../FanslationStudio.LlmKit/docs/features/translation-pipeline/quality-review-pass.md` and labeling it "sibling
repo — the actual `QualityReviewWorkflow` implementation lives there, not in this repo."
