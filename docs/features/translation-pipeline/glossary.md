# Glossary feature

The glossary provides consistent term translations to the primary translation and quality-review prompts. It can also make a result fail validation when a configured term is mistranslated or appears where the source did not contain it.

Glossary entries are loaded at configuration time and stored as `GlossaryLine` records in `LlmConfig.Runtime.GlossaryLines`. The glossary is separate from `ManualTranslations`: glossary entries guide the model and participate in validation, while manual translations are direct authored replacements.

## Files and loading order

A project can use three glossary sources:

1. **Embedded Chinese glossary presets** in the library, enabled by `glossaryPreset.usePresetChineseGlossary` by default.
2. **Workspace glossary files** under `<workingDirectory>/Glossary/`.
3. **`ManualTranslations.yaml`**, which is loaded separately as direct overrides rather than ordinary glossary entries.

Workspace files are YAML lists of `GlossaryLine` objects. The loader reads every file directly under `Glossary/`; file names are organizational and do not select a special glossary category.

Workspace entries replace an existing preset or workspace entry when `Raw`, `RawSimplified`, or `RawTraditional` matches. Otherwise they are appended. This makes a workspace entry the place to override a preset term for a game. Keep the matching raw key stable when overriding; changing the raw spelling creates a new entry instead.

The preset can be disabled or narrowed in `Config.yaml`:

```yaml
glossaryPreset:
  usePresetChineseGlossary: false
  chineseGlossaryTypesToSupress:
    - CommonStats
    - Phonetics
```

The suppression list uses the `ChineseGlossaryTypes` names shipped by the library, such as `GameTerms`, `ItemsAndMinerals`, `Places`, `Titles`, `Weapons`, `WuxiaTerms`, and `XianxiaTerms`.

## Basic entry

```yaml
- raw: 太祖长拳
  result: Taizu Long Fist
```

`raw` is the source spelling to match, and `result` is the preferred translation. A glossary entry is considered for a split only when the containing `TextFileToSplit` has `EnableGlossary: true`.

A fuller entry can provide script variants, alternatives, validation behavior, and file scoping:

```yaml
- raw: 门派
  rawSimplified: 门派
  rawTraditional: 門派
  result: sect
  allowalt:
    - school
  misuse: true
  badtrans: true
  only:
    - GameData/Characters.csv
  exclude:
    - GameData/Tutorial.csv
```

The YAML aliases are:

| YAML key | `GlossaryLine` property | Meaning |
| --- | --- | --- |
| `raw` | `Raw` | Primary source spelling. |
| `rawSimplified` | `RawSimplified` | Simplified-Chinese variant. |
| `rawTraditional` | `RawTraditional` | Traditional-Chinese variant. |
| `result` | `Result` | Preferred translation. |
| `allowalt` | `AllowedAlternatives` | Translations accepted in addition to `result` by glossary validation. |
| `misuse` | `CheckForMisusedTranslation` | Detect the result when it appears without the source term. |
| `badtrans` | `CheckForBadTranslation` | Require the result, or an allowed alternative, when the source term appears. |
| `only` | `OnlyOutputFiles` | Restrict the entry to the listed output paths. |
| `exclude` | `ExcludeOutputFiles` | Prevent the entry from applying to the listed output paths. |

`Direct`, `Literal`, and `Context` are preserved as `GlossaryLine` data fields, but the shared prompt builder currently emits the matched raw variant, `result`, and `allowalt` values. Do not assume those three fields alter translation behavior unless a consuming project adds its own handling.

## Matching and prompt behavior

For each translation or QC prompt:

1. If glossary use is disabled for the file, no glossary prompt is added.
2. Otherwise, entries are checked against `Raw`, then `RawSimplified`, then `RawTraditional` when those values are present in the source text.
3. `only` and `exclude` are applied against `TextFileToSplit.Path`.
4. Matching entries are rendered into the model's base glossary prompt as a YAML-like list containing the source variant, preferred result, and alternatives.

The prompt contains only entries whose raw text occurs in the current source text. A glossary entry does not force a term into unrelated prompts, and an entry with no matching raw variant is not included.

`AllowedAlternatives` are prompt suggestions and validation exceptions. Keep them narrow: every alternative tells the validator that the model may use that wording instead of the preferred result.

## Translation cache and file scoping

Unrestricted glossary entries are seeded into the run-wide translation cache as `Raw -> Result`. Repeated short strings can therefore reuse the authored glossary result without another LLM call.

Entries with `only` or `exclude` are deliberately not put into that shared cache. A cache hit has no file identity, so caching a file-scoped entry would leak its result into other files. Restricted entries instead stay on the normal file-aware translation path, where their prompt scope is respected.

An `only` entry that exactly matches a split can act as a direct deterministic translation for the named file. An `exclude` entry cannot provide that direct override for excluded files; it is treated as unavailable there.

## Validation behavior

Glossary checks run as part of the shared translation rule evaluation and also apply when a QC correction is being accepted.

### Bad translation (`badtrans`)

When `CheckForBadTranslation` is true, a source containing `Raw`, `RawSimplified`, or `RawTraditional` must produce `Result` or one of `AllowedAlternatives`. A missing expected term marks the translation for retranslation and supplies a failure reason to the correction flow.

This defaults to true. Set `badtrans: false` when the entry is prompt guidance only and should not reject a fluent translation that uses different wording.

### Misused translation (`misuse`)

When `CheckForMisusedTranslation` is true, the validator detects a glossary result appearing in translated text even though the corresponding source term was not present. This catches a model hallucinating a domain term into an unrelated sentence.

This defaults to false. Enable it only for distinctive terms where an unexpected occurrence is genuinely suspicious; ordinary words can produce false positives.

### Alternatives

If the source term is present, any case-insensitive occurrence of an allowed alternative satisfies the bad-translation check. Alternatives also help the glossary analysis avoid reporting compatible containment conflicts.

## Script variants

`GlossaryWorkflow.EnrichWithStandardAndTraditionalChinese` can fill missing simplified/traditional variants using the `ToolGood.Words` conversion library. A consuming project can call it before saving or analyzing a glossary:

```csharp
var entries = GlossaryWorkflow.EnrichWithStandardAndTraditionalChinese(entries);
```

The configuration loader does not automatically enrich every workspace entry. Explicitly authored variants remain the most predictable option when a term has a game-specific spelling.

## Glossary analysis

Before importing a large glossary, use `GlossaryWorkflow.AnalyseGlossaryForIssues` to find:

- duplicate raw text or script variants;
- similar source entries with different results;
- containment conflicts where one entry contains another and their translations are incompatible.

These are diagnostics, not automatic corrections. Review the output before changing entries, especially for short terms that naturally occur inside longer names.

## Recommended glossary workflow

1. Disable preset categories that are not appropriate for the game, if needed.
2. Add game-specific entries under `Glossary/` using stable raw keys.
3. Add explicit simplified/traditional variants for terms where automatic conversion could be wrong.
4. Use `allowalt` only for genuinely accepted translations.
5. Choose `badtrans` and `misuse` based on whether the rule should reject output, not merely inform the model.
6. Use `only`/`exclude` when the same source text needs different translations in different files.
7. Run glossary analysis and inspect duplicates/conflicts.
8. Run `TranslationWorkflow.ApplyAllRulesToCurrentTranslation` after changing glossary validation settings so existing translations are rechecked.
9. Retranslate flagged entries, then run QC if enabled and package normally.

For one exact authored translation that should bypass the LLM, use `ManualTranslations.yaml` instead of a glossary entry. For a repair that depends on file structure rather than terminology, use a `GameHooks` repair or validator callback.

## Troubleshooting

- **The term appears in the prompt but the output ignores it:** check `EnableGlossary`, the raw/variant spelling, and the active file path against `only`/`exclude`.
- **A glossary result leaks into another file:** confirm the entry is scoped with `only` or `exclude`; unrestricted entries intentionally seed the shared cache.
- **A fluent translation is repeatedly retried:** inspect `badtrans` and `allowalt`; set `badtrans: false` or add a narrowly justified alternative if the preferred term is guidance rather than a hard requirement.
- **A common English word is flagged as hallucinated:** disable `misuse` for that entry or use a more distinctive glossary result.
- **Changing the glossary appears to do nothing:** reload configuration and run the rules pass; existing `Converted` translations are not rewritten merely because a YAML entry changed.
