# Translation project workflows

A consuming game project should treat the library workflows as a pipeline. Each stage has a different responsibility and produces the input expected by the next stage:

```text
Game-specific dump/extraction
        |
        v
TextFileType-specific Export*ToCustomFormat
        |
        v
Raw/Export/*.yaml -> Converted/*.yaml
        |
        v
TranslationWorkflow.TranslateLines
        |
        v
(optional) QualityReviewWorkflow.RunAsync
        |
        v
TextFileType-specific Package*Async
        |
        v
Files/Mod/* -> game plugin/runtime injection
```

The library owns the common `TranslationLine`/`TranslationSplit`/`FieldTemplate` pipeline. The consuming project owns game-specific dumping, runtime patching, file lists, and the final orchestration method.

## Recommended project order

### 1. Dump or extract source text

Use the game's own tooling to produce source files and configure one `TextFileToSplit` per file. The extraction format determines which library export workflow applies:

| Source shape | Export workflow | Translation unit |
| --- | --- | --- |
| CSV game data | `CsvGameDataWorkflow.ExportToCustomFormat` | CSV cell, with compound cells decomposed into splits/templates |
| JSON game data | `JsonGameDataWorkflow.ExportToCustomFormat` | JSON property or array element, addressed by `SplitPath` |
| One prefab/UI string per line | `PrefabTextWorkflow.ExportPrefabTextToCustomFormat` | Whole line or compound line |
| IL2CPP hardcoded string fragments | `DynamicStringWorkflow.ExportDynamicStringsToCustomFormat` | Runtime substring fragment |
| Legacy Mono/Cecil dynamic strings | `DynamicStringsCecilWorkflow.ExportDynamicStringsToCustomFormat` | Extracted dynamic-string record |

The dump must preserve the raw text and any runtime identity needed for injection. Do not translate directly from the game's source format: export first so the shared split/template model can preserve placeholders and structure.

### 2. Export into the shared format

Call the matching `Export*ToCustomFormat` method once for each configured file. These methods write `Raw/Export/<path>.yaml` and initialize `Converted/<path>.yaml` only when that converted file does not already exist.

That non-overwrite behavior is important: `Converted` is the accumulated working copy. Re-running export after translation must not erase existing translations. If the source dump changed intentionally, decide whether to reset or merge the affected converted file as a project operation before exporting again.

Configure `SkipColumns` for CSV structural columns such as IDs, icons, paths, and lookup keys. Configure `CompoundFieldSplitterOptions` or the game's placeholder patterns when the default decomposition rules cannot recognize a game-specific token.

Configure terminology under `Glossary/` before translation. Glossary entries affect prompts, validation, and sometimes the translation cache; see the [glossary guide](glossary.md) for entry fields, file scoping, and when to use `ManualTranslations.yaml` instead.

### 3. Translate and apply rules

Use `TranslationWorkflow.TranslateLines(workingDirectory, textFiles, hooks)` for the normal pass. It sends eligible splits to the translation service, applies deterministic cleanup and validation, and persists translation state under `Converted`.

Use `TranslationWorkflow.ApplyAllRulesToCurrentTranslation(...)` when rules, glossary entries, or game hooks changed and existing translations need to be rechecked. This pass does not require a new LLM call for a deterministic repair; it can repair existing translations or flag them for retranslation.

Use `TranslationWorkflow.TranslateLinesBruteForce(...)` only for deliberate cleanup runs. It repeatedly applies rules and translation until no records remain to process or the built-in iteration limit is reached. It is useful after adding a broad rule or importing a large new corpus, but it costs more and should not be the default entry point for every run.

The normal translation loop is:

1. Export new source files, without overwriting accumulated `Converted` files.
2. Run `TranslateLines`.
3. Inspect validation/retranslation counts and logs.
4. Run `ApplyAllRulesToCurrentTranslation` after changing rules, glossary data, or hooks.
5. Repeat translation only for splits flagged for retranslation.

Pass the same `GameHooks` instance to every translation entry point. A hook supplied only to one pass makes behavior depend on which command was run.

### 4. Run quality review after translation is complete

`QualityReviewWorkflow.RunAsync(...)` is an optional second LLM pass. Run it only after ordinary translation has produced complete, valid candidates. It reviews reconstructed columns, not unfinished individual splits, and skips unsafe, missing, or flagged translations.

Enable it through `qualityReview.enabled` in `Config.yaml`. The workflow itself is invoked by code and accepts an optional `sampleSize` for a representative trial before a full run:

```csharp
await QualityReviewWorkflow.RunAsync(
    workingDirectory,
    textFiles,
    sampleSize: null,
    hooks: hooks);
```

Use a sample first when evaluating a new QC model or prompt. After a QC run, use `QualityReviewWorkflow.ApplyRulesToCurrentQcTranslated(...)` when validation or game rules changed and stored QC corrections need to be rechecked. Use `RunBruteForce(...)` only when intentionally retrying rejected QC work across several iterations.

QC must remain after primary translation. It can propose a correction, but acceptance uses the same structural and game-specific validation rules as normal translation. Packaging reads QC state only when the review is fresh and meets the configured score gate.

### 5. Package into the game format

Package only after translation, and after QC if the project uses QC. Dispatch each `TextFileToSplit` by `TextFileType`:

- `RawCsv` -> `CsvGameDataWorkflow.PackageAsync`
- `RawJson` -> `JsonGameDataWorkflow.PackageAsync`
- `PrefabText` -> `PrefabTextWorkflow.PackagePrefabTextAsync`
- `DynamicStringsIL2CPP` -> `DynamicStringWorkflow.PackageDynamicStringsAsync`
- `DynamicStrings` -> `DynamicStringsCecilWorkflow.PackageDynamicStringsCecilAsync`

Packaging reconstructs output from `Converted` and writes `Mod`. It does not update the converted translation state. Treat `RawFallback` and `QcRejected` counts as release-gate information, not as reasons to silently continue.

A project packaging method should collect the returned counts for every file and then perform only game-specific steps that the shared package workflow cannot know, such as copying a required companion file or generating a plugin lookup artifact.

### 6. Inject at runtime

The game plugin or runtime patch consumes `Files/Mod` according to the file type. The library does not know where a particular game's UI, CSV loader, JSON loader, prefab component, or dynamic string call lives.

Runtime injection must use the same identity assumptions as extraction and packaging:

- CSV uses positional columns and the original row shape.
- JSON uses property paths and object keys.
- PrefabText uses exact whole-string lookup.
- IL2CPP `DynamicStrings` uses substring replacement.
- LocalTextString is a translation guard, not a packaging workflow; package it through the underlying source format.

## Where game behavior belongs

Use the shared workflows when the operation is format-agnostic and preserves the standard data model. Add game-specific behavior at the nearest boundary that owns the behavior:

| Game-specific need | Preferred extension point |
| --- | --- |
| Produce a dump or discover runtime strings | Consuming project's dumper/converter |
| Recognize a game-specific placeholder while splitting | `CompoundFieldSplitterOptions.PlaceholderPatterns` |
| Skip a known non-translatable CSV column | `TextFileToSplit.SkipColumns` |
| Repair a deterministic model result before validation | `GameHooks.CustomPostRepair` or `CustomColumnRepair` |
| Reject a result so normal retry/correction can regenerate it | `GameHooks.CustomColumnValidator` |
| Keep an opaque record out of QC | `GameHooks.CustomQcExclusionRule` |
| Normalize only the final emitted value | `GameHooks.CustomPackagingFixup` |
| Apply a game-specific final row/object transform | The consuming project's packaging wrapper, after the shared workflow |
| Patch the game's runtime lookup or display path | The consuming project's plugin/runtime code |

Do not fork a shared workflow merely because a game has a special string. First decide whether the difference is extraction, translation validation, packaging, or runtime injection. Keep the shared workflow when the file shape and reconstruction contract still apply; use a game-owned wrapper or hook when the behavior is genuinely game-specific.

## When to override shared behavior

Override or wrap a workflow when at least one of these is true:

- The game's source format cannot be represented by the workflow's `TranslationLine` identity and reconstruction rules.
- The game requires a different runtime lookup key or replacement semantics.
- A final transformation would corrupt other games if added to the shared workflow.
- The operation needs game assemblies, runtime objects, or a game-specific decompiler unavailable to the library.

Do not override shared behavior for a rule that can be expressed by the existing options or hooks. A fork creates a second pipeline that can drift in validation, QC score-gating, raw fallback, and packaging fixups.

## Safe rerun rules

- Re-exporting should not overwrite an accumulated `Converted` file.
- Re-run translation after adding source rows or resetting selected flags.
- Re-run `ApplyAllRulesToCurrentTranslation` after changing deterministic rules, glossary data, or hooks.
- Re-run QC after ordinary translation has settled; stale QC state is ignored by packaging.
- Re-run packaging whenever converted translations, QC state, or packaging hooks change.
- Do not treat a successful package count as proof that runtime injection found the intended text; verify the game-facing lookup path separately.

## Minimal orchestration example

```csharp
foreach (var textFile in textFiles)
{
    switch (textFile.TextFileType)
    {
        case TextFileType.RawCsv:
            CsvGameDataWorkflow.ExportToCustomFormat(workingDirectory, textFile);
            break;
        case TextFileType.RawJson:
            JsonGameDataWorkflow.ExportToCustomFormat(workingDirectory, textFile);
            break;
        case TextFileType.PrefabText:
            PrefabTextWorkflow.ExportPrefabTextToCustomFormat(workingDirectory, textFile);
            break;
        case TextFileType.DynamicStringsIL2CPP:
            DynamicStringWorkflow.ExportDynamicStringsToCustomFormat(workingDirectory, textFile);
            break;
        case TextFileType.DynamicStrings:
            DynamicStringsCecilWorkflow.ExportDynamicStringsToCustomFormat(workingDirectory, textFile);
            break;
    }
}

await TranslationWorkflow.TranslateLines(workingDirectory, textFiles, hooks);

if (qualityReviewEnabled)
    await QualityReviewWorkflow.RunAsync(workingDirectory, textFiles, hooks: hooks);

foreach (var textFile in textFiles)
    await PackageOneFileAsync(workingDirectory, textFile);
```

`PackageOneFileAsync` is intentionally project-owned because it selects the runtime-facing output and can collect per-file release metrics. The shared packaging guide contains the exact dispatch and fallback behavior for each file type.
