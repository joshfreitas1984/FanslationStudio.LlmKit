# FanslationStudio.LlmKit — Architecture Reference

> Audit snapshot as of 2026-08-23 (updated to reflect the item #1 optimization work — see
> OPTIMIZATION_PLAN.md for the full history). This is reference material for future agent
> conversations and developer onboarding — it describes **what the code does today**, not
> aspirational design. See [OPTIMIZATION_PLAN.md](./OPTIMIZATION_PLAN.md) for proposed/remaining
> changes.

## What this library is

A reusable "over LLM" game-translation kit. It takes structured game text (CSV rows, dynamic
strings, etc.) from a downstream project (e.g. `DragonHeirOverLlm`, `LegendOfMortalOverLlm`),
extracts translatable fragments, sends them to an LLM (local Ollama-style HTTP endpoint or hosted
API), validates/repairs the result, and reassembles it back into the original file shape.

Downstream projects consume it via a **project reference** (`../../FanslationStudio.LlmKit/...`),
not a NuGet package — it's meant to be edited in lockstep with the games that use it.

## Core data model (`Support/`)

```
TranslationLine            (one row/line from the source file)
├── Raw            : string                      — untouched original line
├── Splits         : List<TranslationSplit>       — extracted translatable fragments
│     ├── Split            : int    — column/field index the fragment came from
│     ├── SubIndex         : int    — position within that column (0 for plain columns)
│     ├── Text / Translated
│     └── SafeToTranslate, FlaggedForRetranslation, FlaggedMistranslation, FlaggedHallucination
└── Templates      : List<FieldTemplate>          — reconstruction info for compound columns
      └── { Split, Template }   — original cell with each fragment replaced by "{n}"
```

Golden rule (per repo instructions): this hierarchy is the contract every downstream project
depends on — extend with new optional fields, never change shape, so old serialized YAML keeps
deserializing.

`TextFileToSplit` (also in `Support/`) is the per-file/per-game descriptor: `Path`, `TextFileType`,
`EnableGlossary`, `EnableBasePrompts`, `AdditionalPromptName`, `SkipColumns`, `PackageOutput`.

## Working-directory contract (`Files/` in each downstream project)

```
Raw/Dumped/…        — raw game dumps (input, via BepInEx plugin in downstream repos)
Raw/Export/*.yaml   — freshly exported TranslationLine YAML — disposable, regenerated every export
Converted/*.yaml    — accumulates Translated values across runs — the file that matters
Mod/*.csv           — final packaged output
Config.yaml         — LlmConfig (models, batch size, prompts, glossary preset, split rules)
ManualTranslations.yaml
Glossary/*.yaml     — workspace glossary overrides (merged over preset glossary)
TestResults/OldFiles — historical translated files used to seed the translation cache
```

## Configuration (`Configuration/ConfigurationExtensions.GetConfiguration`)

Single entry point, called once per workflow invocation. Loads, in order:

1. `Config.yaml` → `LlmConfig` (models list, `BatchSize`, `RetryCount`, `SplitCharactersList`,
   `SplitRegexPatterns`, `GlossaryPreset`, `UseContinuousWorkerPool`, `MaxConcurrency`, etc.).
2. `ManualTranslations.yaml` → exact raw→result overrides, always wins over LLM output.
3. Per-model runtime config (`RuntimeValues.Models[name]`): merges a built-in **preset**
   (currently only `Qwen25`, loaded from embedded resources under `BaseFiles/Qwen25/`) with
   workspace-level prompt overrides (`{WorkingDirectory}/{ModelName}Prompts/*.txt`) and an API key
   file (`{ModelName}ApiKey.txt`).
4. Preset Chinese glossary (embedded `BaseFiles/ChineseGlossary/*.yaml`, filterable by
   `ChineseGlossaryTypesToSupress`), then workspace `Glossary/*.yaml` merged on top (workspace
   entries override preset entries matching on `Raw`/`RawSimplified`/`RawTraditional`).
5. Hyphens in glossary/manual results are rewritten to non-breaking hyphens (Unity line-break
   workaround).

Adding a new preset model = add a case in `GetQwen25Preset`-style method + embedded
`BaseFiles/<Name>/Config.yaml` + prompt `.txt` resources.

## Entry points (`Workflow/TranslationWorkflow.cs`)

- `TranslateLines` → single pass, `TranslationService.TranslateViaLlmAsync(force:false)`.
- `TranslateLinesBruteForce` → loop: apply validation rules (flagging bad/retranslatable lines) →
  translate → re-validate → repeat, up to 30 iterations or until nothing is flagged. This is the
  "keep hammering until clean" mode used for real translation runs.
- `ApplyAllRulesToCurrentTranslation` → just the validation/flagging pass, no LLM calls (cheap,
  used to see what *would* need retranslating).

`UpdateCurrentTranslationLines` iterates all files **in parallel**
(`FileIteration.IterateTranslatedFilesInParallelAsync`) and, within a file, all lines **in
parallel** (`Parallel.ForEach`) running `ProcessLine`/`UpdateSplit` — a chain of pure/local rule
checks (glossary hits, bad regex, dynamic-string exclusion, manual translation match, empty
check, bad-word/mistranslation/hallucination glossary checks, `...` ellipsis repair, trim,
diacritics cleanup, final `LineValidation.CheckTransalationSuccessful`). None of this touches the
LLM — it's why it can safely run fully parallel.

## Translation pipeline (`TranslationService.TranslateViaLlmAsync`) — see also OPTIMIZATION_PLAN.md

This is the part that actually calls the LLM. `TranslateViaLlmAsync` is a thin dispatcher over two
schedulers, selected by `LlmConfig.UseContinuousWorkerPool` (`Config.yaml: useContinuousWorkerPool`,
default `false`):

- **`TranslateViaLlmAsyncBatched`** — the original scheduler:

  ```
  foreach textFile in textFiles                      (SEQUENTIAL — one file at a time)
      load Converted/<file>.yaml (or seed from Raw/Export on first run)
      for i in 0..totalLines step batchSize            (SEQUENTIAL — one batch at a time, hard barrier)
          batch = lines[i .. i+batchSize)
          uniqueSplits = distinct-by-Text splits in this batch only
          await Task.WhenAll(uniqueSplits.Select(TranslateSplitAsync))   (PARALLEL within batch)
          propagate result to duplicate splits within the same batch
          every 25 records written: flush Converted/<file>.yaml to disk (synchronous write)
      final flush for the file
  ```

  `batchSize` (`Config.yaml: batchSize`) is the *only* concurrency knob here — it caps how many
  distinct strings are in flight at once, and doubles as a checkpoint boundary (files rewritten to
  disk after each batch's writes accumulate past `BatchlessBuffer` = 25). Every batch is a hard
  synchronization barrier and files are fully sequential — the tail latency of one slow item (a
  retry/correction/sub-split chain) holds up the whole batch, and there's no overlap between one
  file's last few items and the next file's first ones.

- **`TranslateViaLlmAsyncPooled`** (recommended; see OPTIMIZATION_PLAN.md "Validation result") —
  a continuous worker pool across the whole run instead of sequential batches/files:

  ```
  load every file up-front, build PooledFileState per file (output path, lines, serializer, lock, counters)
  workItems = every file's distinct-by-Text splits, flattened across ALL files into one list
  await Parallel.ForEachAsync(workItems, MaxDegreeOfParallelism = MaxConcurrency ?? BatchSize ?? 20)
      translate-or-cache-hit each item, independent of file/batch boundaries
      periodically (every 25 processed for that file): propagate duplicates for that file, flush to disk
  final pass per file: propagate remaining duplicates, final flush
  ```

  No batch barrier and no file-boundary barrier — a worker pulls the next unique string the moment
  it finishes one, so a file's slow tail naturally overlaps with the next file's first items.
  Per-file dedup semantics (`EnableGlossary`/`AdditionalPromptName`/etc.) are preserved — only the
  *scheduling* is flattened across files, not the translation prompt logic. Duplicate propagation
  is opportunistic (runs before every buffered flush, not just once at the end) so a
  cancelled/killed run only loses the unique-split translations and duplicate-links made since
  that file's last flush — restart-safety is comparable to (arguably better than) the batched
  scheduler. Confirmed faster than the batched scheduler on a real translation run (informal
  wall-clock comparison, not yet formal p95/in-flight-concurrency numbers — see
  OPTIMIZATION_PLAN.md).

Key mechanics shared by both schedulers:
- **Translation cache** (`FillTranslationCacheAsync`, `RuntimeValues.TranslationCache` — a
  `ConcurrentDictionary<string,string>`, not a plain `Dictionary`, since both schedulers mutate it
  from parallel workers): built once per run, seeded from manual translations, preset+workspace
  glossary, `TestResults/OldFiles/*.yaml` history, and any already-translated split ≤
  `TranslationService.TranslationCacheMaxChars` (50, raised from an original 10) chars from the
  current output files. Cache hits skip the LLM entirely for short/medium repeated strings (names,
  common short phrases); longer one-off sentences are deliberately excluded so the cache doesn't
  grow unbounded with strings unlikely to recur verbatim.
- **Per-split translation** (`TranslateSplitAsync`) short-circuits in this order: empty input →
  no-Chinese-detected → game-object-reference heuristic (`LocalTextString` files) → bracket/regex
  pre-split (`SplitBracketsRegexIfNeededAsync`) → configured split-characters segmentation
  (`SplitOnCharsIfNeededAsync`, recursive, first successful split wins) → half-color-tag handling →
  cache hit → real LLM call.
- **`SplitBracketsRegexIfNeededAsync`** and **`SplitOnCharsIfNeededAsync`** translate their
  independent pieces concurrently (`Task.WhenAll` via the shared `TranslatePiecesWithRetryAsync`
  helper) and only retry the piece(s) that actually failed validation — a single stubborn piece no
  longer discards every sibling piece that already translated correctly.
  `SplitBracketsRegexIfNeededAsync` builds a `{n}`-placeholder template for the full sentence in
  one forward pass and restores bracket contents via `CompoundFieldSplitter.Reconstruct` (the same
  safe substitution used for CSV-cell fragments elsewhere), instead of hand-rolled
  index/offset string surgery. **This also fixed a real bug**: the old restoration logic computed
  each bracket's translated content and then discarded it, splicing in a mangled substring of an
  internal placeholder *number* instead — bracketed text translated via this path silently lost
  its translation. Confirmed via git history that this path was live on real `DragonHierOverLlm`
  translation runs (`splitRegexPatterns` had bracket patterns enabled before being disabled again) —
  see `TranslationWorkflowTests.SetBracketSplitBugLinesAsInvalid` in the downstream repo for a
  one-off remediation workflow step that flags affected lines for retranslation.
- **Real LLM call** (`TranslateMessagesAsync`): builds prompt via `GenerateBaseMessages`, POSTs
  once, retries on HTTP 429 with exponential backoff (5s → up to 60s, max 5 retries, now logging
  per-attempt wait time and a post-backoff summary of total blocked time), strips `<think>` tags
  from reasoning-model output.
- **Validation + correction loop** (`LineValidation.CheckTransalationSuccessful` + `RetryCount`):
  on failure, regenerates a fresh message list (to avoid unbounded context growth) and appends a
  correction prompt. If the failure is a "leftover Chinese characters" case, the outer retry loop
  keeps re-invoking `CorrectSentenceBySentenceAsync` (feeding the previous attempt's output back in
  as the next input) instead of immediately falling back to a fresh whole-cell retranslation —
  since that function only re-translates sentences still containing Chinese characters,
  already-corrected sentences from a prior attempt are never re-sent to the LLM. Only once the
  sentence-level retry budget (`RetryCount`) is exhausted does it fall back to a normal whole-cell
  correction message. `CorrectSentenceBySentenceAsync` itself corrects its sentences **concurrently**
  (`Task.WhenAll`), using a dedicated `SplitIntoSentences` helper (splits on `.`/`!`/`?` followed by
  whitespace or end-of-string, tracking brace depth so it never splits inside a `{n}` placeholder
  and double-quote state so it never splits inside a quoted clause) instead of a naive
  `Split(". ")`.
- **Duplicate propagation**: batched scheduler is per-batch (`GroupBy(split => split.Text)` within
  one batch's lines); pooled scheduler is per-file (a strict superset — duplicates split across two
  batches of the same file are always linked). Neither scheduler dedups across *different* files —
  only the run-wide translation cache does that, and only for strings ≤
  `TranslationCacheMaxChars`.

## Validation (`LineValidation.cs`) & correction

Central gatekeeper for "is this translation acceptable" — Chinese-character-leftover detection,
placeholder/token preservation checks, HTML/color/size tag integrity, length/format
sanity. Returns a `ValidationResult` with a `CorrectionPrompt` describing exactly what's wrong so
the retry attempt can self-correct rather than blindly re-asking.

## String/text utilities (`Utility/`)

- `CompoundFieldSplitter` — CSV parsing + compound-cell fragment decomposition/reconstruction
  (see `.github/copilot-instructions.md` in this repo and
  `DragonHierOverLlm/.github/instructions/tests-translation-workflow.instructions.md` for the full
  rules — this is the most heavily documented piece of logic in the kit).
- `StringTokenReplacer` — swaps game-specific tokens (placeholders, extra tokens from
  `Config.yaml: extraStringTokenReplacers`) out before sending to the LLM and back in after.
- `ColorTagHelpers`, `HtmlTagHelpers` — Unity rich-text tag extraction/preservation.
- `LlmHelpers` — request payload construction, per-text model selection
  (`CalculateModelConfig` — chooses Standard vs StructuredText model based on text shape).
- `YamlHelper` — shared YamlDotNet serializer/deserializer configuration.

## Testing conventions

- `Tests/` (in this repo) — genuine fast xUnit unit tests against pure utilities
  (`UtilityTests.cs`, `WildCardMatchingServiceTests.cs`) and against `TranslationService`'s
  LLM-calling helpers using a **mocked LLM HTTP endpoint**
  (`TranslationServiceTests.cs`/`ScriptedLlmHandler` — an `HttpMessageHandler` that scripts
  responses per rule, matched against the concatenation of every message's content in the
  request). This lets per-piece-retry, bracket-reconstruction, sentence-splitting, and
  outer-retry-scoping behavior be regression-tested without a real LLM or a live translation run —
  the whole suite runs in well under a second. Safe to run as a batch/CI suite.
- Downstream `DragonHierOverLlm/Tests/` — **not** a regression suite; numbered, manually-run
  workflow steps that mutate real `Files/` state and call the live LLM. Never run as a batch. See
  that repo's own `tests-translation-workflow.instructions.md`.
- Rule of thumb ported from that instructions file: prefer targeted assertions on
  `CompoundFieldSplitter.Decompose(...)`'s `Template`/`Fragments` output over eyeballing YAML or
  round-trip-only checks.
- **When adding LLM-calling logic in this repo, prefer a `ScriptedLlmHandler`-based unit test over
  a manual live-LLM validation run** — bugs like the bracket-restoration one (see above) are only
  caught reliably by an assertion on the actual returned string, not by eyeballing translated
  output during a real run.

## Known extension points (where a new game/project plugs in)

- `TextFileToSplit[]` list (per-file config: type, glossary on/off, skip columns, prompt name).
- `CompoundFieldSplitterOptions.PlaceholderPatterns` — per-game dynamic-token regex.
- `Config.yaml` — models, batch size, retry/correction toggles, split characters/regex.
- `{ModelName}Prompts/*.txt` — override any base/dynamic prompt without forking the preset.
- `Glossary/*.yaml`, `ManualTranslations.yaml` — data-only overrides, no code changes needed.
