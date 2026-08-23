# FanslationStudio.LlmKit — Architecture Reference

> Audit snapshot as of 2026-08-23. This is reference material for future agent conversations and
> developer onboarding — it describes **what the code does today**, not aspirational design. See
> [OPTIMIZATION_PLAN.md](./OPTIMIZATION_PLAN.md) for proposed changes.

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
   `SplitRegexPatterns`, `GlossaryPreset`, etc.).
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

This is the part that actually calls the LLM, and the part under review for batching efficiency.

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

Key mechanics:
- **Batch size** (`Config.yaml: batchSize`, currently `100` in `DragonHeirOverLlm/Files`) is the
  *only* concurrency knob — it caps how many distinct strings are in flight at once, and doubles as
  a checkpoint boundary (files rewritten to disk after each batch's writes accumulate past
  `BatchlessBuffer` = 25).
- **Translation cache** (`FillTranslationCacheAsync`): built once per run, seeded from manual
  translations, preset+workspace glossary, `TestResults/OldFiles/*.yaml` history, and any already-
  translated split ≤ `charsToCache` (10) chars from the current output files. Cache hits skip the
  LLM entirely for short repeated strings (names, single words).
- **Per-split translation** (`TranslateSplitAsync`) short-circuits in this order: empty input →
  no-Chinese-detected → game-object-reference heuristic (`LocalTextString` files) → bracket/regex
  pre-split (`SplitBracketsRegexIfNeededAsync`, translates bracket contents then the whole
  sentence with placeholders) → configured split-characters segmentation
  (`SplitOnCharsIfNeededAsync`, recursive, first successful split wins) → half-color-tag handling →
  cache hit → real LLM call. Each of these branches that does call the LLM (brackets, split-chars,
  color halves) does so **sequentially inside the branch**, not in parallel with sibling work.
- **Real LLM call** (`TranslateMessagesAsync`): builds prompt via `GenerateBaseMessages` (dynamic
  prompt assembly based on cues in the text — color tags, size tags, HTML-ish tags, placeholders,
  glossary), POSTs once, retries on HTTP 429 with exponential backoff (5s → up to 60s, max 5
  retries), strips `<think>` tags from reasoning-model output.
- **Validation + correction loop** (`LineValidation.CheckTransalationSuccessful` +
  `RetryCount`): on failure, regenerates a fresh message list (to avoid unbounded context growth)
  and appends a correction prompt; if the failure is a "leftover Chinese characters" case, falls
  back to `CorrectSentenceBySentenceAsync` which corrects each sentence **sequentially** with its
  own LLM call.
- **Duplicate propagation**: within one batch only — `GroupBy(split => split.Text)` over that
  batch's lines, first occurrence translated, rest copy its result. Duplicate text that lands in a
  *different* batch or a *different* file is not deduped this way (only caught by the ≤10-char
  cache).

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
  (`UtilityTests.cs`, `WildCardMatchingServiceTests.cs`). Safe to run as a batch/CI suite.
- Downstream `DragonHierOverLlm/Tests/` — **not** a regression suite; numbered, manually-run
  workflow steps that mutate real `Files/` state and call the live LLM. Never run as a batch. See
  that repo's own `tests-translation-workflow.instructions.md`.
- Rule of thumb ported from that instructions file: prefer targeted assertions on
  `CompoundFieldSplitter.Decompose(...)`'s `Template`/`Fragments` output over eyeballing YAML or
  round-trip-only checks.

## Known extension points (where a new game/project plugs in)

- `TextFileToSplit[]` list (per-file config: type, glossary on/off, skip columns, prompt name).
- `CompoundFieldSplitterOptions.PlaceholderPatterns` — per-game dynamic-token regex.
- `Config.yaml` — models, batch size, retry/correction toggles, split characters/regex.
- `{ModelName}Prompts/*.txt` — override any base/dynamic prompt without forking the preset.
- `Glossary/*.yaml`, `ManualTranslations.yaml` — data-only overrides, no code changes needed.
