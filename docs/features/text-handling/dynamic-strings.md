# Dynamic string workflows

Dynamic string workflows translate hardcoded text that is assembled at runtime rather than stored as an ordinary CSV, JSON, or prefab-text file. Choose the workflow that matches the game's runtime and patching strategy.

## Choose a workflow

| `TextFileType` | Runtime model | Input | Packaged output | Use when |
| --- | --- | --- | --- | --- |
| `DynamicStrings` | Mono/Cecil dump and runtime transpiler patch | Five comma-separated fields per line | `DynamicStringContract` entries keyed by type, method, and IL offset | The game is Mono and the consuming plugin patches methods by Cecil metadata. |
| `DynamicStringsIL2CPP` | Harmony postfix plus substring replacement | One hardcoded literal per line | `DynamicStringResult` entries containing `raw`, `result`, and `isTemplate` | The game is IL2CPP and literals were identified from decompiled code. |

These are separate workflows. Do not use the legacy `DynamicStrings` format for IL2CPP dummy assemblies: they do not contain usable method bodies for a Cecil transpiler.

## Legacy `DynamicStrings` workflow

Use `DynamicStringsCecilWorkflow` for the Mono/Cecil format.

### Input and export

Configure a `TextFileToSplit` with `TextFileType.DynamicStrings`. The default input path is:

`Raw/Dumped/{textFile.Path}`

Each dumped line must contain five comma-separated fields:

`Type,Method,ILOffset,RawText,ParamTypesList`

The dumper must replace commas inside `RawText` and `ParamTypesList` with the fullwidth comma `，`, because this exporter uses a deliberately simple comma split. The exporter finds Chinese text fields, normally field 3 (`RawText`), removes surrounding quotes, and writes the standard `TranslationLine` YAML to:

- `Raw/Export/{textFile.Path}.yaml`
- `Converted/{textFile.Path}.yaml` if that file does not already exist

The converted file is preserved on later exports so existing translations are not overwritten.

To use another dump folder, pass the folder to `ExportDynamicStringsToCustomFormat` through its `rawSubfolder` parameter.

### Translation and package

Translate the exported split through the normal translation workflow. Then call:

```csharp
await DynamicStringsCecilWorkflow.PackageDynamicStringsCecilAsync(
    workingDirectory,
    textFile);
```

The package step writes `Mod/{textFile.Path}.yaml` as a list of `DynamicStringContract` records. Each record uses the original `Type`, `Method`, and `ILOffset` to identify the runtime method, restores fullwidth commas in the translated text, parses the parameter list, and is emitted only when `DynamicStringSupport.IsSafeContract` accepts it.

A line is counted as `RawFallback` and omitted when it does not have exactly one split, has no usable translation, or is flagged for retranslation. Unsafe contracts are omitted without increasing the failure count. This legacy workflow does not produce `QcRejected`; its result tuple always reports zero for that bucket.

## `DynamicStringsIL2CPP` workflow

Use `DynamicStringWorkflow` for hardcoded literals discovered from a consuming project's decompiled IL2CPP code. The input is a hand-curated list, not an automated scan of IL2CPP dummy assemblies.

### Input and export

Configure a `TextFileToSplit` with `TextFileType.DynamicStringsIL2CPP`. Put one distinct literal per nonblank line at:

`Raw/Dumped/DynamicStrings/{textFile.Path}`

The exporter converts the flat list into the normal `TranslationLine` shape and writes:

- `Raw/Export/{textFile.Path}.yaml`
- `Converted/{textFile.Path}.yaml` if that file does not already exist

A literal `\\n` in the dump is restored to a real newline before decomposition so the exported `Raw` value matches the runtime string. Compound literals use the normal `CompoundFieldSplitter` rules and can produce a `FieldTemplate` with multiple fragments.

### Translation and package

Translate and merge through the normal pipeline, then call:

```csharp
await DynamicStringWorkflow.PackageDynamicStringsAsync(
    workingDirectory,
    textFile);
```

The package step writes `Mod/{textFile.Path}.yaml` as a flat list:

```yaml
- raw: "架势"
  result: "Posture"
  isTemplate: false
```

The consuming IL2CPP plugin applies ordinary entries as exact substring replacements against assembled runtime strings. Entries containing `{0}`, `{1}`, or game localization markers such as `#TargetInteractName#` are marked `isTemplate: true`; the runtime must apply those entries to the template arguments before `String.Format` or equivalent substitution removes the placeholders.

For a compound literal with a single translatable fragment and a literal metadata suffix, packaging also emits a deduplicated bare-label entry. This supports runtime strings where the game consumes the label but does not display the metadata suffix.

Unusable lines are omitted from the dictionary and counted as `RawFallback`. A rejected QC correction falls back to ordinary `Translated` text when that text is usable; the package result reports `QcRejected` only when the rejected correction leaves no usable translation.

## Downstream integration checklist

- Register the correct `TextFileType` and dispatch export and package calls explicitly.
- Keep `DynamicStrings` and `DynamicStringsIL2CPP` data in their expected input layouts.
- Preserve `Converted/{textFile.Path}.yaml` between exports so translations can be merged.
- Apply legacy `DynamicStringContract` output through a Mono/Cecil-compatible runtime patch.
- Apply IL2CPP `DynamicStringResult` output through the consuming plugin's substring/template patch.
- Report `(Passed, QcRejected, RawFallback)` from the package workflow.

## Troubleshooting

- Empty legacy output usually means the dump did not contain a Chinese `RawText` field, the line did not split into exactly five fields, or the contract was rejected by `IsSafeContract`.
- Missing IL2CPP replacements usually means the literal is not present in the hand-curated dump exactly as it appears at runtime, including newline handling.
- A template entry that never matches may have been packaged as a plain entry; check the `isTemplate` field and the runtime patch's template handling.
- Re-exporting over an existing `Converted` file does not refresh its contents. Remove or archive that converted file only when deliberately starting a new translation set.
