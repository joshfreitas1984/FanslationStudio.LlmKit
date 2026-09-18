# CSV and JSON game-data workflows

This guide covers the shared workflows for structured game data:

- `TextFileType.RawCsv` uses `CsvGameDataWorkflow`.
- `TextFileType.RawJson` uses `JsonGameDataWorkflow`.

Both workflows convert dumped game data into the common `TranslationLine` -> `TranslationSplit` -> `FieldTemplate` representation, then rebuild game-consumable files during packaging. Use these workflows when the game data is naturally represented as CSV rows or JSON objects. Use the [compound-field splitting guide](../compound-field-splitting/compound-field-splitting.md) for the fragment extraction rules shared by both formats.

## Shared file lifecycle

A consuming project normally runs these stages for each configured `TextFileToSplit`:

1. Dump or copy the source file into the expected raw folder.
2. Call the format's `ExportToCustomFormat` method.
3. Translate the generated `Raw/Export/{path}.yaml` data into `Converted/{path}.yaml`.
4. Optionally run the rules pass and quality review.
5. Call the format's `PackageAsync` method to write `Mod/{path}`.
6. Let the game-specific plugin or loader consume the packaged file.

Export never overwrites an existing `Converted/{path}.yaml`. This preserves accumulated translations when a project re-exports the same source file. Packaging reads `Converted` and writes `Mod`; it does not mutate the converted translation state.

## CSV workflow

### Export

`CsvGameDataWorkflow.ExportToCustomFormat` reads one file from:

```text
{workingDirectory}/Raw/Dumped/GameData/{textFile.Path}
```

The `rawSubfolder` argument can override `Raw/Dumped/GameData` when a project stores dumped CSV files elsewhere. Each source row becomes a `TranslationLine` whose `Raw` value is the untouched CSV row. Rows are parsed with `CompoundFieldSplitter.ParseCsvRow`, so quoted commas and arbitrary column counts are preserved.

Each non-skipped cell is passed to `CompoundFieldSplitter.Decompose`. A plain translatable cell becomes one `TranslationSplit` with its zero-based column in `Split`. A compound cell gets a `FieldTemplate` for that column and one split per fragment, ordered by `SubIndex`. Cells with no translatable fragments produce no splits.

Use `TextFileToSplit.SkipColumns` for IDs, icons, resource paths, lookup keys, and other structural columns. Skipped cells are not decomposed and remain source-controlled during packaging.

The export writes:

```text
Raw/Export/{textFile.Path}.yaml
Converted/{textFile.Path}.yaml   # created only when it does not exist
```

### Packaging

`CsvGameDataWorkflow.PackageAsync` writes the reconstructed rows to:

```text
Mod/{textFile.Path}
```

For each row it:

1. Parses the original `TranslationLine.Raw` back into CSV fields.
2. Reconstructs templated columns from translated fragments.
3. Writes translated plain columns directly into their original positions.
4. Leaves skipped columns untouched from the original parsed row.
5. Applies standard and game-specific packaging fixups.
6. Rebuilds the row with `CompoundFieldSplitter.RebuildCsvRow`.

A row falls back to its original raw CSV line if any packageable split is unsafe, flagged for retranslation, missing its translation, or the file has `PackageOutput: false`. CSV packaging is row-granular: one failed column prevents the partially translated row from being emitted.

The optional hooks are useful for game-specific integration:

- `onColumnPackaged`: observes `(column, rawText, packagedText)` after a column is prepared.
- `rowPostProcess`: changes the complete field array immediately before CSV reconstruction.

Neither hook runs for a row that falls back to its original raw text.

## JSON workflow

### Supported source shape

`JsonGameDataWorkflow.ExportToCustomFormat` reads a JSON array of objects. Each object must have a `Key` property; objects without one are ignored. The key becomes `TranslationLine.RawIndex`, which is the stable identity used when translated files are merged back together. The original object JSON is retained in `TranslationLine.Raw` so packaging can preserve fields that were not translated.

The default source location is:

```text
{workingDirectory}/Raw/Dumped/{textFile.Path}
```

Pass `rawSubfolder` to use a different dump location. Exported translatable values are:

- JSON string properties, addressed by their property name, such as `Name` or `Desc`.
- String elements inside arrays, addressed by a property path such as `ChatList[0]`.

Non-string values and empty strings are ignored. The shared compound splitter handles each string, so a field such as `Desc: "你好;再见"` can produce a template and multiple fragments just like a CSV cell.

The current JSON workflow also skips sibling properties whose normalized name ends in `Tw` or `Final`. This is a game-data convention for alternate or finalized values, not a universal JSON rule. If a different game needs another convention, implement that decision in the consuming project or extend the shared workflow deliberately with tests.

Export writes the same YAML locations as CSV:

```text
Raw/Export/{textFile.Path}.yaml
Converted/{textFile.Path}.yaml   # created only when it does not exist
```

### Packaging

`JsonGameDataWorkflow.PackageAsync` writes a formatted JSON array to:

```text
Mod/{textFile.Path}
```

For each translated line it:

1. Parses the original object from `TranslationLine.Raw`.
2. Restores `Key` from `TranslationLine.RawIndex`.
3. Reconstructs each templated property or array element from its fragments.
4. Writes the result to its `SplitPath`.
5. Preserves untouched properties, non-string values, skipped siblings, and array elements.

JSON packaging is field-granular. If one field is unsafe, flagged, or missing a translation, that field remains at its original raw value while other translated fields in the same object are still emitted. The returned `RawFallback` count records those failed fields.

## Quality review and fallback

Both workflows use the shared QC freshness and score-gate helpers. A fresh accepted `QcTranslated` value can replace the ordinary translation. If QC is stale, disabled, or below the configured score gate, packaging falls back to the ordinary `Translated` value; a low QC score alone does not cause CSV or JSON packaging failure.

Both workflows return:

```text
(Passed, QcRejected, RawFallback)
```

For CSV and JSON, `QcRejected` remains zero because a rejected QC correction falls through to the ordinary translation. `RawFallback` means the ordinary translation was not packageable. CSV reports failed rows; JSON reports failed fields.

See the [packaging workflow guide](../packaging/packaging-workflows.md) for the full QC and fallback matrix.

## Downstream wiring

A consuming project's packaging method should dispatch by `TextFileType`:

```csharp
foreach (var textFile in textFiles.Where(f => f.TextFileType == TextFileType.RawJson))
    await JsonGameDataWorkflow.PackageAsync(workingDirectory, textFile);

foreach (var textFile in textFiles.Where(f => f.TextFileType == TextFileType.RawCsv))
    await CsvGameDataWorkflow.PackageAsync(workingDirectory, textFile);
```

The consuming project still owns the dump location, configured file list, runtime loader or patch, and any game-specific post-processing. Keep format-independent fragment parsing, reconstruction, QC gating, and fallback behavior in the shared workflows.

## Troubleshooting

- **CSV columns shift after packaging:** check that export and packaging use the shared CSV parser/rebuilder and that no project code uses `line.Split(',')`.
- **A CSV ID or path changes unexpectedly:** add its zero-based column to `SkipColumns` and re-export before packaging.
- **A JSON translation is not found:** verify the object has a stable `Key` and that the translated YAML retains `RawIndex` and the expected `SplitPath`.
- **Only part of a JSON object is translated:** inspect the per-field `RawFallback` result; one failed field is intentionally preserved while other fields package.
- **A JSON `Tw` or `Final` value changes:** those sibling properties should not be exported as translatable splits; check the source property naming and the generated YAML.
- **A compound CSV cell or JSON field is malformed:** inspect its `FieldTemplate` and fragment ordering, then test `CompoundFieldSplitter.Decompose` and `Reconstruct` directly.
