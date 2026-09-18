# PrefabText workflow

Use this workflow for flat, row/column-less text files whose lines contain translated text and optional structured placeholders. The model contract is defined in [the architecture guide](../../architecture/ARCHITECTURE.md).

## Use this workflow when

Use `TextFileType.PrefabText` for a dumped list of hardcoded UI or prefab text: one distinct string per line, with no CSV structure. Any game with a similar flat-list dumper can reuse `PrefabTextWorkflow` without game-specific workflow code.

## Export

`ExportPrefabTextToCustomFormat` reads `Raw/Dumped/PrefabText/{textFile.Path}` as plain text, skips blank lines, and emits the standard `TranslationLine` YAML shape. Each source line becomes one whole-line `TranslationSplit` (`Split = 0`, `SubIndex = 0`) with no `FieldTemplate`.

## Translate and merge

The normal translation and retry pipeline handles these entries unchanged. Merge uses the ordinary line and split identities, and `CheckFileLinesMatch` applies through `FileIteration`.

## Package

`PackagePrefabTextAsync` writes a flat `List<PrefabTextResult>` to `Mod/{textFile.Path}.yaml`, with `raw` and `result` values rather than CSV reconstruction. If no usable translation exists (`Translated` is empty, the split is flagged for retranslation, or it is not safe to translate), it falls back to the source `Text`. A consuming project must exclude `PrefabText` entries from CSV `ParseCsvRow` reconstruction and call this packaging path instead.

## Downstream integration checklist

- Register `TextFileType.PrefabText` with workflow and packaging dispatch.
- Preserve line and split identities through export, translation, merge, and re-export.
- Keep `PrefabText` entries out of CSV reconstruction.
- Use `PackagePrefabTextAsync` after translation and QC.

## Troubleshooting

- Missing or reordered strings usually indicate that source line order or identity was not preserved.
- A missing output entry indicates that the prefab packaging path was not used.
- Source text in `result` is the expected fallback for empty, flagged, or unsafe translations.
