# AGENTS.md

Universal rules for any AI coding agent working in this repository, regardless of vendor. This
file exists so no repository rule lives in only one vendor-specific format
(`.github/copilot-instructions.md`, `CLAUDE.md`, etc.) — mirrors the same pattern used by
downstream consuming repos (e.g. `DragonHierOverLlm`).

## Start here

- [`docs/README.md`](docs/README.md) is the canonical documentation hub — what this repo is,
  the `plans`/`investigations`/`features` taxonomy, source-of-truth rules, and a "where should I
  look?" task table.
- [`.github/copilot-instructions.md`](.github/copilot-instructions.md) is the current-state source
  of truth for rules and safety invariants (`applyTo: "**"` — this repo has no sub-projects to
  scope by path). Read it before making changes.

## Repository-wide rules

- This is a **shared library** consumed by downstream "over LLM" translation projects via a
  project reference, not a NuGet package — changes here take effect immediately in every
  downstream repo without a version bump. Consider whether a change is genuinely game-agnostic
  before making it here versus in a downstream repo's own game-specific hook.
- **The golden rule:** the `Line → Splits → (Templates)` data model shape is the contract every
  downstream project depends on. Extend it with new optional fields (safe defaults), never change
  its shape — old serialized YAML in a downstream repo's `Files/Converted/*.yaml` must keep
  deserializing correctly.
- Do not update instructions files, `docs/KNOWN_ISSUES.md`, or `docs/` topic files as a side effect of a
  fix or feature. Only write documentation when explicitly asked to.
- **Reverse-engineering rule:** when you investigate/reverse-engineer how existing code in this
  repo works, write down what you learned in this repo's own `docs/` (a new or extended topic
  file, indexed from `docs/KNOWN_ISSUES.md`) before finishing the task, even if not explicitly asked —
  findings that only exist in chat history are lost for future sessions. This applies even when
  you're investigating LlmKit-internal behavior from a downstream repo's session — record it here,
  not in the downstream repo's own notes, since this is the sibling repo the logic actually
  belongs to.
- Keep auto-loaded instructions files short and operational. Long rationale, design-history, and
  bug-fix postmortems belong in a linked document under `docs/`, not the instructions file itself.
- Prefer fast, pure xUnit unit tests against static utilities (`CompoundFieldSplitter`,
  `TranslationServiceTests`'s `ScriptedLlmHandler`-mocked-HTTP tests) over anything that would
  require a live LLM/Ollama call — this repo's own `Tests/` is a genuine CI-safe regression suite,
  unlike a downstream repo's numbered, manually-run workflow tests.

## Where to look for more detail

See [`docs/README.md`](docs/README.md)'s "Where should I look?" table for task-specific starting
points (compound-field splitting, translation retry/escalation, PrefabText workflow, the
in-progress quality-review-pass feature, etc.).
