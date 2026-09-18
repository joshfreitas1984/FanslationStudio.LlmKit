# FanslationStudio.LlmKit — known-issue index

> This file is an **index only**. It is not auto-loaded into agent context (unlike
> `.github/copilot-instructions.md`, which has `applyTo: "**"`). Detailed investigation narratives
> Detailed investigation narratives and postmortems live under `docs/investigations/` — read only
> the specific document relevant to your current task. When a new issue investigation is written,
> add a one-line pointer here; keep plans, design history, architecture, and feature documentation
> in their own `docs/` categories instead.

## Investigations and postmortems

- [`docs/investigations/translation-retry-escalation-and-fixes.md`](docs/investigations/translation-retry-escalation-and-fixes.md)
  — `TranslationService` retry/escalation mechanics, plus real-run postmortems: a correction-suffix
  prompt leak causing repeated false `Unprocessable` entries (fixed 2026-08-28),
  `ApplyAllRulesToCurrentTranslation` not applying game-specific hooks to already-translated lines
  (fixed 2026-09-08, plus a follow-up false-positive fix for tokens with an embedded digit), and a
  dropped-closing-tag bug in a runtime-color-placeholder template that `HtmlTagHelpers.ValidateTags`'
  set-based (not count-based) comparison let through — includes a reverted first-attempt fix (a new
  per-split `GameHooks.CustomTranslationExclusionRule` hook, which turned out to be the wrong layer
  since `CompoundFieldSplitter` decomposes these raw strings before any single split sees the whole
  template) before landing on the actual fix: a whole-raw/whole-result override in the consuming
  repo's existing packaging-time override mechanism (fixed 2026-09-17).

