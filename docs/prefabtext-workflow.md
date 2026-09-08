# `TextFileType.PrefabText` (`Workflow/PrefabTextWorkflow.cs`) — flat, row/column-less files

> Extracted from `.github/copilot-instructions.md` during the Sep 2026 docs restructuring (see
> `AGENTS.md`'s workflow rule).

Added Aug 2026 as the **standard, game-agnostic** way to handle a dumped list of hardcoded
UI/prefab text (baked directly into `MonoBehaviour`/`TMP_Text` components rather than a CSV) — see
`DragonHeirOverLlm`'s `Tests/AssetDumperWorkflowTests.cs` for the concrete asset-scanning dumper
that produces this kind of input file (one distinct string per line, no other structure). Any game
with a similar flat-list dumper can reuse `PrefabTextWorkflow` as-is with zero game-specific code.

- `Support/TextFileToSplit.cs`'s `TextFileType` enum already had a `PrefabText` value before this
  workflow existed — it just wasn't wired up anywhere.
- `PrefabTextWorkflow.ExportPrefabTextToCustomFormat(workingDirectory, textFile)` reads
  `Raw/Dumped/PrefabText/{textFile.Path}` (plain text, one string per line, blank lines skipped)
  and produces the same `TranslationLine` YAML shape the CSV export path uses — each line becomes
  exactly one whole-line `TranslationSplit` (`Split = 0, SubIndex = 0`) with **no `FieldTemplate`**
  (there's no column structure to reconstruct around). This means it's a drop-in `TextFileToSplit`
  entry for every other generic pipeline stage: `GameFileHandlingBase.MergeFilesIntoTranslatedAsync`,
  `Workflow/TranslationWorkflow.cs`'s translate/retry loop, and `CheckFileLinesMatch` all work on it
  unmodified via `FileIteration`.
- `PrefabTextWorkflow.PackagePrefabTextAsync(workingDirectory, textFile)` is the packaging
  counterpart — **NOT** CSV-shaped like `PackageFinalTranslationAsync`'s `Reconstruct`/`{n}`
  substitution. It writes a flat `List<PrefabTextResult>` (`Support/PrefabTextResult.cs`, a plain
  `{ Raw, Result }` pair with no CSV cell/template concept at all) to `Mod/{textFile.Path}.yaml`:
  ```yaml
  - raw: 地图一览
    result: Map Overview
  ```
  (camelCase YAML keys via `YamlHelper`, same as everything else.) Falls back to `Result = Text`
  (the untranslated source) when a split has no usable translation yet (empty `Translated`,
  `FlaggedForRetranslation`, or `!SafeToTranslate`), so every dumped string always gets an output
  entry. A consuming project's `PackageFinalTranslationAsync` must filter `TextFileType.PrefabText`
  entries OUT of its CSV `ParseCsvRow` reconstruction loop and call this instead — a plain-string
  `Raw` line has no comma/quote structure to parse as CSV.

**Golden rule (repeated here for emphasis):** the Line → Splits → (Templates) hierarchy is the
contract every downstream project depends on. When adding new capability, extend it with new
optional fields (with safe defaults) rather than changing its shape — old serialized YAML must keep
deserializing correctly. `PrefabText`'s design (a plain single-split line, no template) is a good
example of fitting a new, structurally simpler kind of input into the existing model without
changing it.
