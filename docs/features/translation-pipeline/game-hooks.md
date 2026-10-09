# GameHooks extension points

`GameHooks` provides runtime-only, game-specific callbacks at defined points in the shared translation pipeline. Use hooks for deterministic rules that depend on one game's file format or runtime conventions; keep the common workflows game-agnostic.

## Registration

Hooks are not read from `Config.yaml`. Create them in the consuming project and pass the same instance to configuration and every workflow entry point:

```csharp
var hooks = new GameHooks
{
    CustomPostRepair = (raw, result) => RepairKnownModelQuirk(raw, result),
    CustomColumnRepair = (file, column, raw, result) => RepairColumn(file, column, raw, result),
    CustomColumnValidator = (file, column, raw, result) => ValidateColumn(file, column, raw, result),
    CustomQcExclusionRule = (file, column, raw) => IsOpaqueRecord(file, column, raw),
    CustomUnsafeToTranslateRule = (file, line, split) => IsNonDisplayText(file, line, split),
    CustomPackagingFixup = (file, column, raw, result) => FixPackagedText(file, column, raw, result),
    LineContextProvider = (workingDirectory, file, lines) => BuildLineContexts(workingDirectory, file, lines),
    UnknownGenderPersonTokens = ["#PlayerName#", "#$PlayerName#"],
};

var config = ConfigurationExtensions.GetConfiguration(workingDirectory, hooks);
```

A hook supplied to only one command makes behavior depend on which command was run. Forward it through translation, QC, and any configuration-loading wrapper.

## Execution order

| Hook | Runs | Return value | Choose it when |
| --- | --- | --- | --- |
| `CustomPostRepair` | After standard LLM-result normalization | Repaired result | The repair applies globally and needs only raw/result text. |
| `CustomColumnRepair` | After post-repair, before validation | Repaired result | The repair is specific to a file or zero-based column. |
| `CustomColumnValidator` | After built-in validation | `null` if valid, otherwise a failure reason | The result must be rejected and sent through normal retry/correction handling. |
| `CustomQcExclusionRule` | Before QC work items and before any QC LLM call | `true` to exclude | A selected column is machine-readable or opaque and should never be reviewed. |
| `CustomUnsafeToTranslateRule` | In the rules pass (`ApplyAllRulesToCurrentTranslation`, `TranslateLinesBruteForce`), before any other per-split rule | `true` to mark the split unsafe | An entry, identified by its whole line (e.g. a dynamic string's call site), must never be translated. |
| `CustomPackagingFixup` | After standard packaging fixups, before output is written | Final output text | The adjustment belongs only in emitted game files. |
| `LineContextProvider` | Once per file when a translation run starts, only if `lineContextEnabled` is true in `Config.yaml` | A `LineContext` per split | The game knows something the text alone does not, such as who is speaking and their gender. |

For a normal translation attempt, repair callbacks run first, then built-in validation, then the custom validator. A non-null validator reason participates in the normal retranslation/correction flow. `ApplyAllRulesToCurrentTranslation` re-applies the repair and validator hooks to existing converted translations, so deterministic hook changes do not require a full retranslation just to reach old entries.

## Callback contracts

### `CustomPostRepair`

`Func<string, string, string>` receives `(raw, result)`. Return the minimally repaired result. Use it for a known model formatting quirk that is safe across the consuming game's translated text. It is still validated afterward; do not use it to accept an uncertain result.

### `CustomColumnRepair`

`Func<TextFileToSplit?, int?, string, string, string>` receives `(textFile, column, raw, result)`. The file or column can be null outside a CSV-column context. Use it for a deterministic repair that is valid only for a particular file or column, such as removing a separator that cannot occur in that field.

### `CustomColumnValidator`

`Func<TextFileToSplit, int?, string, string, string?>` receives `(textFile, column, raw, result)`. Return `null` when valid; otherwise return a concise reason suitable for a correction prompt. This is a rejection rule, not a repair rule. Use `CustomColumnRepair` when the correction is deterministic.

The validator is also applied by the shared translation rule evaluation used by ordinary translation and QC correction acceptance. A game-specific rejection therefore cannot be bypassed by accepting a QC correction.

### `CustomQcExclusionRule`

`Func<TextFileToSplit, int?, string, bool>` receives the file, column, and reconstructed raw column text. Return `true` to omit the column from the QC pass entirely. This differs from validation: exclusion prevents a QC LLM call, while validation permits a candidate and then rejects it when it violates a rule. Use the per-file quality-control setting when the whole file should be excluded; use this hook for selected columns.

### `CustomUnsafeToTranslateRule`

`Func<TextFileToSplit, TranslationLine, TranslationSplit, bool>` receives the file, the whole line (so `TranslationLine.Raw` is available, not just the split text), and a split that is still `SafeToTranslate`. Return `true` to set `SafeToTranslate = false`; the split is then skipped by translation, QC, and packaging, and the file is rewritten. The flag is never reset automatically, so removing a rule does not re-enable splits it already marked. Plain `TranslateLines` does not run the rules pass, so new entries are only marked once `ApplyAllRulesToCurrentTranslation` or `TranslateLinesBruteForce` has run. Example: marking every dynamic string whose `Raw` starts with a specific `Type/<Method>` call site, where the literal is an asset key rather than display text.

### `LineContextProvider`

Receives `(workingDirectory, textFile, lines)` and returns a `LineContext(Prompt, GenderKnown, Gender)` for each split it has context for. The lines arrive in file order, so a provider can carry state across rows (for example the current speaker). `Prompt` is appended to that split's system prompt, including every recursive sub-call (leading brackets, color tags, bracket splits). `GenderKnown` tells the pipeline that a he/she is correct there, which suppresses the soft invented-gender correction and lets `PronounDefectWorkflow` check old translations against `Gender`.

A split with a context never reads or writes the shared translation cache and never deduplicates against identical text with a different context, because the same text can translate differently for a different speaker. Splits the provider omits translate exactly as before. Never return a gender you cannot know: a player-chosen gender, or an unnamed character, should get a prompt that says the gender is unknown and `GenderKnown: false`.

#### `CharacterContext.AddCharacterContext` (named characters)

When the game has a table of character names and genders, a provider calls the shared helper instead of writing its own name matching:

```csharp
var characters = CharacterContext.FromCsv(path, nameColumn: "名字", genderColumn: "性别", stripFromNames: ".");
// or CharacterContext.FromYaml(path): a list of { name, gender, aliases } entries
CharacterContext.AddCharacterContext(contexts, lines, characters, minNameLength: 3, skipSplit: split => HasPlayerToken(split));
```

Any split that names a character gets "the text names X, a female character: use she/her for them, otherwise they". It never replaces a context that already knows a gender (a speaker) and skips a split naming characters of different genders. Genders are `male`/`female` (or `男`/`女`, `m`/`f`); other values are ignored. Set `minNameLength` to keep short names that are ordinary words out of the table's matching; list a short name explicitly (with the minimum lowered) only when it is safe.

This does not raise more flags in the rules pass. A known-gender context is checked with `ContradictsGender`: the right pronoun passes, only one that contradicts the table is flagged, and the invented-gender rule (and `autoRepairPronouns`) never applies to it. What it can add is true positives: a line naming a female character that says "he" is now a `WrongGender` hit where before it was only a guess. A false positive remains when the line also contains an unnamed person of the other gender.

### `UnknownGenderPersonTokens`

A list of placeholder tokens (for example `#PlayerName#`) that stand for a person whose gender is not known when the text is translated: the player, or someone the game fills in at runtime. It is not a callback. `LineValidation.InventsGender` treats a he/she/his/her/him in a line containing one of them as an invented gender when the source states no gender itself (no 他/她, no kinship term or title), and skips the line when the translation names someone else, since the pronoun may be theirs. Survey the game's tokens first: list every `#...#` token in the raw text and keep only those that are people. Faction, place, item and number tokens are not people. A `LineContextProvider` should give the same lines a "gender unknown, use you/they" hint so new translations avoid the problem.

### `CustomPackagingFixup`

`Func<TextFileToSplit?, int?, string, string, string>` receives `(textFile, column, raw, result)` after the standard packaging fixups. It runs for CSV, JSON, PrefabText, and dynamic-string packaging. It changes only the emitted package value; it does not update `Converted` state or trigger translation retries.

## Choosing an extension point

- Game-specific extraction or runtime string discovery: the consuming project's dumper/converter.
- Game-specific placeholder recognition: `CompoundFieldSplitterOptions.PlaceholderPatterns`.
- Non-translatable CSV columns: `TextFileToSplit.SkipColumns`.
- Deterministic translation repair: `CustomPostRepair` or `CustomColumnRepair`.
- A result that must be regenerated: `CustomColumnValidator`.
- Content QC must never inspect: `CustomQcExclusionRule`.
- Entries that must never be translated, identified by line context: `CustomUnsafeToTranslateRule`.
- Final output normalization: `CustomPackagingFixup`.
- A final row/object transformation or runtime patch: the consuming project's packaging wrapper or plugin.

Keep hooks deterministic, narrow, and game-specific. Avoid network calls, mutable global state, or rules that depend on which workflow happened to invoke them. Do not fork a shared workflow for a behavior that fits an existing option or hook.

## Troubleshooting

- A hook never runs: confirm the consuming project passes it into configuration or the top-level workflow; YAML cannot register it.
- A repaired value is still retried: inspect built-in validation and the custom validator reason; repair runs before both.
- QC still reviews an excluded column: verify the rule returns `true` for the reconstructed raw text and is attached before `QualityControlWorkflow.RunAsync` builds its work list.
- A split is still translated despite `CustomUnsafeToTranslateRule`: run `ApplyAllRulesToCurrentTranslation` (or brute-force translation) with the hooks attached; plain `TranslateLines` does not evaluate it.
- Packaged output changes while `Converted` does not: this is expected for `CustomPackagingFixup`, which is output-only.
