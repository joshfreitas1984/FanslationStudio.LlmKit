# LocalTextString translation guard

`TextFileType.LocalTextString` protects local game-object references from being sent to the LLM. It is a translation-time special case, not a standalone export or packaging workflow.

## Use this type when

Use `LocalTextString` for a file whose strings can contain both user-facing Chinese text and game-object paths such as UI hierarchy references. The current detector recognizes a string containing `/` together with one of these markers:

- `View`
- `btn`
- `Part`
- `Text`

Examples include paths such as `Canvas/MainView/TitleText` or `UI/Confirmbtn/PartText`. The detector is intentionally heuristic and is applied only when the file is configured as `LocalTextString`.

## Translation behavior

The guard runs in both translation paths:

- `TranslationService.TranslateSplitAsync` returns the original string without an LLM call when it matches the game-object-reference detector.
- `TranslationWorkflow.UpdateSplit` applies the same check to existing converted lines. If a reference was accidentally changed, it restores `Translated` to the original `Text` and clears the split's translation flags.

Non-matching strings continue through the normal cleanup, glossary, validation, and LLM translation pipeline. The type does not mean that every string in the file is automatically preserved.

## Export and packaging

`LocalTextString` does not have a dedicated exporter or package method. Use the file's existing export and packaging workflow, such as the CSV or JSON workflow that owns its source format. The value only changes translation decisions for the resulting `TranslationSplit` entries.

Because the detector is heuristic, keep the file's normal raw and converted data available for inspection. A matching reference remains unchanged in the converted data and should be emitted by the owning packaging workflow in the same way as other untranslated-but-safe source values.

## Configuration

Set the per-file type in the consuming project's `TextFileToSplit` configuration:

```yaml
- path: "local-text.csv"
  textFileType: LocalTextString
```

The exact path and surrounding file settings depend on the source format. Do not use this type as a replacement for `PrefabText`, `DynamicStrings`, or `DynamicStringsIL2CPP`; those types select dedicated file workflows.

## Troubleshooting

- A string was translated even though it is a reference: confirm the file uses `LocalTextString`, then check whether it contains `/` and one of the recognized markers.
- Ordinary prose was preserved unexpectedly: the heuristic matched a slash and marker combination. Move that content to a different file type or use a game-specific workflow/configuration boundary.
- The file is not exported or packaged: `LocalTextString` does not provide those stages. Configure the file with the exporter and package workflow appropriate to its actual source format.
