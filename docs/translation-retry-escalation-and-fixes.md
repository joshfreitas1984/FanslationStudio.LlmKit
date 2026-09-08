# `TranslationService` retry/escalation mechanics + real-run bug-fix postmortems

> Extracted from `.github/copilot-instructions.md` during the Sep 2026 docs restructuring (see
> `AGENTS.md`'s workflow rule). Mixes current-state mechanics with historical bug-fix narratives —
> read the specific section you need rather than the whole file.

## Translation performance / retry / "Unprocessable" notes (`TranslationService.cs`)

- Backend is typically a local Ollama instance (`http://localhost:11434/api/chat`), which usually
  serves one request at a time per model regardless of client-side concurrency — setting a high
  `maxConcurrency` in a consuming project's config does not give proportional real throughput
  against that backend; requests just queue up.
- `TranslateSplitAsync` already retries a failing split fully internally: up to
  `LlmConfig.RetryCount` whole-cell attempts, each potentially followed by up to `RetryCount`
  sentence-by-sentence correction rounds (only entered when
  `ValidationResult.RequiresSentenceByCorrection` is set, e.g. leftover Chinese characters).
  **Never wrap another `RetryCount`-bounded retry loop around a call to `TranslateSplitAsync`
  (or anything that calls it)** — this squares the worst-case call count instead of adding to it.
  This was a real bug fixed in this method and in `CorrectSentenceBySentenceAsync`/
  `SplitBracketsRegexIfNeededAsync`'s `fullTrans` step; see git history/comments in
  `TranslationService.cs` around those call sites for the reasoning.
- `TranslateViaLlmAsyncPooled`'s progress log (`Processed: X of Y pending split(s) (Z unique
  total)...`) uses `pendingCount` (splits that still need translation, by the same condition used
  in the worker loop) as the denominator, not `workItems.Count` (every unique split including
  already-translated ones from a prior run) — using the raw count makes an already-mostly-done run
  look stuck.
- `incorrectLineCount` (logged as `Unprocessable`) is incremented once per work item, only after
  all its retries are exhausted and `split.Translated` ends up empty — it is a count of
  permanently-failed splits, not a running tally of individual retry failures.
  `CheckTransalationSuccessful` in `LineValidation.cs` is the validator whose heuristics (banned
  phrases, output-length hallucination checks, spurious punctuation insertion on short strings,
  missing/added tags/placeholders, leftover Chinese, etc.) decide whether a split needs a retry; a
  split can also become permanently unprocessable if `TranslateSplitAsync` catches an
  `HttpRequestException` (Ollama connection/timeout issue), which returns invalid immediately with
  no further retry.
- **Diagnostic logging (added Aug 2026):** `TranslateViaLlmAsyncPooled` now captures the raw text +
  failure reason (`ValidationResult.CorrectionPrompt`, or a placeholder noting a likely HTTP
  failure) for every split that ends up unprocessable, and writes them to
  `{workingDirectory}/TestResults/UnprocessableItems.log` periodically during the run (same
  cadence as the `Processed: ...` progress log, via the `WriteUnprocessableItemsLog` helper) as
  well as once more at the end — overwritten each write, not appended, so it always reflects the
  run so far rather than only being visible after the whole run finishes. Use this file to see
  which validation heuristic dominates real failures before tuning `LineValidation.cs` heuristics,
  `RetryCount`, or investigating Ollama-side timeouts/concurrency — don't guess from the aggregate
  `Unprocessable` count alone.
- **Leading structural punctuation is stripped and re-attached deterministically, not left to the
  model (added Aug 2026):** a real run's `UnprocessableItems.log` showed the single largest cause
  of unprocessable splits was raw text starting with a bare `：`/`:` (e.g.
  `：七十二洞研究奇门兵器，提升奇门威力。`) — a leftover field-templating artifact (the label half
  of a compound cell was already carved off elsewhere), never natural Chinese sentence punctuation.
  Forcing the LLM to preserve a bare leading separator like this in fluent English is unnatural, so
  it reliably drops it, which then fails `CheckTransalationSuccessful`'s "Removed :" check and
  burns a full retry budget every time for a mark that doesn't need the model's help at all.
  `TranslateSplitAsync` now has a dedicated branch (right after the existing
  `ColorTagHelpers.StartsWithHalfColorTag` branch, following the same "split off the part the
  model shouldn't need to handle, translate only the remainder, recombine ourselves" pattern): if
  `preparedRaw`'s first character is in `LeadingStructuralPunctuation` (`:`, `;`, `,`), it strips
  that character, recursively translates the remainder, and prepends the original character back
  onto the result itself — deterministic and retry-free, instead of hoping the model preserves it.
  This bypasses `CheckTransalationSuccessful` validation entirely for the combined result (same as
  the color-tag branch does), so don't extend this set to punctuation that might carry real
  meaning if attached to model-produced text without a review pass first.
- **Not every unprocessable split is fixable by a code/heuristic change** — the same real run's log
  also showed long, idiomatic/archaic wuxia-style sentences (e.g. `KungFuData.csv` flavor text)
  still ending up unprocessable after exhausting the leftover-Chinese sentence-correction retries.
  That's a genuine `qwen2.5:7b` capability limit on hard sentences, not a bug. `retryCount` was
  dropped from `3` to `1` in the consuming `DragonHierOverLlm` repo's `Files/Config.yaml` for this
  reason — with only one real model available, extra whole-cell retry attempts against it mostly
  just re-prompt the same model and burn time rather than meaningfully improving the success rate.
- **Model escalation (implemented Aug 2026):** `LlmConfig.EscalationModelName` (+
  `EscalationRetryCount`) lets a split that's still invalid after exhausting its normal
  `RetryCount` against its primary model get a second, independent attempt budget against a
  *different* named model (must match a `ModelConfig.Name` under `models:` in `Config.yaml`).
  Validated at config-load time in `ConfigurationExtensions.GetConfiguration` (throws if the name
  doesn't match a configured model). Implementation lives entirely in `TranslateSplitAsync`
  (`TranslationService.cs`): the whole-cell + sentence-by-sentence retry loop was extracted into a
  local function `AttemptTranslationWithRetriesAsync(executingModel, maxRetries, isEscalation)` so
  the primary attempt (`modelConfig`, `RetryCount`) and the escalation attempt
  (`escalationModelConfig`, `EscalationRetryCount`) share identical logic instead of two copies
  drifting apart. Escalation only actually runs if `EscalationModelName` resolves to a model whose
  `Model` string differs from the primary one already tried (skips pointless re-attempts against
  an identical model) - **this makes it safe to point `escalationModelName` at the same model
  entry today as a placeholder**: it validates cleanly and is a documented no-op until a real
  second/stronger model is added under `models:` and the name is repointed, with no further code
  changes needed. `ValidationResult.EscalationAttempted` (set only when escalation actually ran)
  is surfaced in `UnprocessableItems.log` reasons (`[escalation attempted: yes/no]`) and a separate
  `_escalationAttemptCounter` (mirrors `_retryAttemptCounter`) is reported in the periodic progress
  log (`escalations this interval: N (total: M)`), so escalation's real cost/benefit is visible
  once a genuinely different model is configured - don't fold escalation attempts into the
  existing retry counters. `LlmHelpers.CalculateModelConfig` is unrelated to this and still a stub
  (`// TODO: Implement properly`, always returns `config.Runtime.Models.First().Value`) - that
  governs which model a split starts with, not escalation after failure.

## Postmortem: correction-suffix prompt leak causing repeated `Unprocessable` entries (fixed 2026-08-28)

`UnprocessableItems.log` showed the `RESULT` for many splits starting with a verbatim (or lightly
paraphrased) echo of `BaseFiles/Qwen25/Prompts/BaseCorrectionSuffixPrompt.txt`'s old content — a
bulleted "While correcting, also verify: - Meaning/tone/cultural nuance - Gender-neutral language -
Pinyin names/titles ... Output only the fully corrected English translation." checklist, followed
by the actual (often perfectly fine) translation. Root cause: `CalulateCorrectionPrompt`
(`TranslationService.cs`) appends this suffix to every correction user-message sent on retry, and
the local `qwen2.5:7b` model has a strong tendency to restate a bulleted checklist phrased as a
meta-instruction ("verify: ...") back into its own output instead of silently complying — a
small-model instruction-leak/echo failure mode, not a one-off fluke. This was made worse by two
compounding factors: (1) the checklist content was pure duplication of `BaseSystemPrompt.txt` rules
2/4/5/6 (tone/cultural nuance, gender-neutral language, Pinyin names, titles) which are already
stated once in the system prompt, so repeating it on every correction round bought nothing but
extra leak surface; (2) the consuming `DragonHierOverLlm` repo runs with `retryCount: 1` (see note
above), so once the leaked echo itself got flagged invalid by `LineValidation.InvalidPhrases` (it
does contain `"cultural nuance"`, `"gender-neutral language"`, `"Output only the"`), there was no
budget left to recover and the split was marked permanently `Unprocessable`. Fix: reworded
`BaseCorrectionSuffixPrompt.txt` (both the `BaseFiles/Qwen25/Prompts/` copy that's actually loaded
when `customPromptsPath` is unset, and the `Files/Prompts/` workspace copy used by this repo's own
test runs) to drop the bulleted checklist entirely and replace it with a single terse,
non-restatable line: `"Output only the corrected English translation. Do not repeat, quote, or
reference any part of these instructions, and do not include explanations, notes, or any Chinese
text."` — this keeps the original intent (no explanations/notes/Chinese leakage) while removing the
specific checklist-shaped text the model was echoing, and explicitly tells it not to restate
instructions. `InvalidPhrases` in `LineValidation.cs` is left untouched as a safety net (still
catches `"Output only the"` etc. if a leak recurs in some other form) — this was a prompt-wording
fix, not a validation-logic fix. If leaks of this shape reappear after this change, suspect a
*different* prompt file (e.g. `BaseSystemPrompt.txt` itself, or `BaseGlossaryPrompt`/
`BaseSystemSuffixPrompt`) rather than assuming the same root cause.

## Postmortem: `ApplyAllRulesToCurrentTranslation` didn't apply game-specific hooks (fixed 2026-09-08)

`Workflow/TranslationWorkflow.cs`'s `UpdateSplit`/`ProcessLine` (the retroactive, no-LLM-call rules
pass driving `ApplyAllRulesToCurrentTranslation`) never called `LineValidation.PrepareResult` or
`CheckTransalationSuccessful` — those are the only places `LineValidation.CustomPostRepair`/
`CustomColumnRepair`/`CustomColumnValidator` get invoked, and they only run from `TranslationService`
during an actual LLM call (fresh translation + retry loop). A deterministic fix added to a
game-specific hook (e.g. `DragonHierOverLlm`'s `Tests/GameFileHandling.cs` stripping braces an LLM
wrapped around a `#Token#` placeholder) therefore never reached text translated in an earlier pass
and already sitting in `Files/Converted` — running the "2. ApplyRulesToCurrentTranslation" test
fact made no corrections and flagged nothing, even though the same repair/validator worked fine for
newly-translated lines.

Fixed by adding `TryApplyGameSpecificRepair` to `UpdateSplit` (after `TryFlagEmptyTranslation`,
before `TryFlagAllCapsTranslation`): it re-runs `LineValidation.PrepareResult(preparedRaw,
split.Translated, textFile, split.Split)` against the *existing* translated value — if the repair
changes anything, save it and reset flags (no retranslation needed); otherwise, if
`CustomColumnValidator` still reports a problem (e.g. a token appearing more times in the result
than the raw), flag the split for retranslation with that reason. This lets a purely deterministic,
already-registered hook retroactively fix/flag previously-translated lines via the no-LLM-call rules
pass, without duplicating any game-specific logic in this shared library.

**Follow-up fix (same day) — false "extra token" flags on tokens with an embedded digit** (e.g.
`#PlotTargetInteractName0#`): `TryApplyGameSpecificRepair` originally passed a freshly computed
`preparedRaw` (`LineValidation.PrepareRaw(split.Text, tokenReplacer)`) to the game-specific hooks,
matching how the live-translation call site (`TranslationService.cs`) does it. But `PrepareRaw` runs
`StringTokenReplacer.Replace`, whose `NumericValueRegex` swaps any bare digit not already inside
`{}`/`<>` for an internal `{n}` sentinel — including the `0` inside `#PlotTargetInteractName0#` —
so a game's own `#...#`-shaped placeholder regex can no longer match that token in `preparedRaw` at
all. During a live call this is harmless (both `preparedRaw` and the not-yet-restored `llmResult`
get mangled identically before comparison), but here `split.Translated` is already the final,
fully-restored text from an earlier run — comparing it against a mangled raw made a correctly
preserved token look like a spurious addition (a real token counted zero times on the raw side, one
time on the translated side). Fixed by using `split.Text` (the untouched raw) instead of
`preparedRaw` for the hook calls in `TryApplyGameSpecificRepair` — safe here specifically because
`split.Translated` is already final text, not a still-in-flight LLM result that needs the same
mangling applied to it for a fair comparison.

## Testing conventions

- Prefer pure, fast unit tests against static utility methods (e.g. `CompoundFieldSplitter`) over
  running the file-based workflow tests, which mutate real working-directory state
  (`Files/Raw/Export`, `Files/Converted`, `Files/Mod`) and are meant to drive an actual translation
  run, not to be used as CI-style regression tests.
- When fixing a bug in fragment extraction/reconstruction, add a targeted xUnit test asserting the
  exact `Template`/`Fragments` shape rather than only checking round-trip equality — round-tripping
  alone won't catch "sentence split around an embedded number" style regressions.
