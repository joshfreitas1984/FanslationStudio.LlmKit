using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Standard, game-agnostic handling for <see cref="TextFileType.RawCsv"/> files - genuine CSV game
/// data, parsed/rebuilt via <see cref="CompoundFieldSplitter.ParseCsvRow"/>/
/// <see cref="CompoundFieldSplitter.RebuildCsvRow"/> (quote-aware, arbitrary column count) rather
/// than a naive delimiter split. Mirrors <see cref="PrefabTextWorkflow"/>/
/// <see cref="DynamicStringWorkflow"/>'s per-file API shape: one <see cref="TextFileToSplit"/> per
/// call, dispatched by whatever loop a consuming project already runs over its own
/// <c>TextFileToSplit[]</c> list, filtered to <see cref="TextFileType.RawCsv"/> entries.
/// </summary>
public static class CsvGameDataWorkflow
{
    /// <summary>
    /// Reads a dumped CSV file from <paramref name="rawSubfolder"/>/{textFile.Path} (default
    /// "Raw/Dumped/GameData", matching the original convention this generalizes), decomposes every
    /// non-<see cref="TextFileToSplit.SkipColumns"/> cell via
    /// <see cref="CompoundFieldSplitter.Decompose"/>, and writes the same TranslationLine YAML
    /// shape the PrefabText/DynamicStrings export paths use into Raw/Export and (if not already
    /// present) Converted. <paramref name="rawSubfolder"/> exists because not every consuming
    /// project dumps its CSVs to the same place - it's a parameter rather than a second hardcoded
    /// convention.
    /// </summary>
    public static void ExportToCustomFormat(
        string workingDirectory, TextFileToSplit textFile, CompoundFieldSplitterOptions? options = null,
        string rawSubfolder = "Raw/Dumped/GameData")
    {
        var dumpedPath = $"{workingDirectory}/{rawSubfolder}/{textFile.Path}";

        var lines = File.ReadAllLines(dumpedPath);
        var foundLines = new List<TranslationLine>();

        foreach (var line in lines)
        {
            var splits = CompoundFieldSplitter.ParseCsvRow(line);
            var foundSplits = new List<TranslationSplit>();
            var foundTemplates = new List<FieldTemplate>();

            for (int i = 0; i < splits.Length; i++)
            {
                if (textFile.SkipColumns.Contains(i))
                    continue;

                var (template, fragments) = CompoundFieldSplitter.Decompose(splits[i], options, textFile.EnableSizeShrink);
                if (fragments.Count == 0)
                    continue;

                if (CompoundFieldSplitter.IsTrivialTemplate(template, fragments.Count))
                {
                    foundSplits.Add(new TranslationSplit(i, 0, fragments[0]));
                    continue;
                }

                foundTemplates.Add(new FieldTemplate(i, template));

                for (int f = 0; f < fragments.Count; f++)
                    foundSplits.Add(new TranslationSplit(i, f, fragments[f]));
            }

            foundLines.Add(new TranslationLine
            {
                Raw = line,
                Splits = foundSplits,
                Templates = foundTemplates,
            });
        }

        ExportHelpers.WriteExport(workingDirectory, textFile, foundLines);
    }

    /// <summary>
    /// Packages a translated CSV file into Mod/{textFile.Path}, reconstructing compound-template
    /// columns via <see cref="CompoundFieldSplitter.Reconstruct"/> and applying the QC
    /// freshness/score gate exactly like <see cref="PrefabTextWorkflow.PackagePrefabTextAsync"/>
    /// does for a single-column file. Any <see cref="TextFileToSplit.SkipColumns"/> column is left
    /// completely untouched from the row's original parse - this is a correctness requirement, not
    /// an optimization: an earlier hand-rolled version of this loop (see a consuming project's own
    /// history/KNOWN_ISSUES for the incident) skipped this check for one of the two column kinds
    /// and, separately, let a stale multi-fragment leftover in a skipped column overwrite the cell
    /// with only its last fragment's raw text - both silent corruption bugs a shared, tested
    /// implementation prevents for every consumer at once.
    ///
    /// <paramref name="onColumnPackaged"/> is an optional per-column callback (column index, raw
    /// text, packaged text) for a consuming project that needs to observe what was written per
    /// column without LlmKit knowing why (e.g. collecting a side-channel lookup file from specific
    /// columns). <paramref name="rowPostProcess"/> is an optional final fixup over the whole row's
    /// fields just before <see cref="CompoundFieldSplitter.RebuildCsvRow"/> - e.g. a workaround for
    /// one game's own naive CSV loader misreading a trailing comma before a closing quote. Neither
    /// hook runs for a row that fell back to its original raw text.
    /// </summary>
    /// <param name="config">Already-loaded configuration; read from <paramref name="workingDirectory"/> when null.</param>
    public static async Task<(int Passed, int QcRejected, int RawFallback)> PackageAsync(
        string workingDirectory, TextFileToSplit textFile,
        Action<int, string, string>? onColumnPackaged = null,
        Func<string[], string[]>? rowPostProcess = null,
        LlmConfig? config = null)
    {
        var outputPath = $"{workingDirectory}/Mod";
        Directory.CreateDirectory(outputPath);

        config ??= ConfigurationExtensions.GetConfiguration(workingDirectory);
        var qualityControl = config.QualityControl;

        var outputLines = new List<string>();
        var counts = new PackagingCounts();

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory, [textFile], async (_, _, fileLines) =>
        {
            foreach (var line in fileLines)
            {
                var splits = CompoundFieldSplitter.ParseCsvRow(line.Raw);
                var failed = false;
                var templatedColumns = line.Templates.Select(t => t.Split)
                    .Where(s => !textFile.SkipColumns.Contains(s)).ToHashSet();

                foreach (var template in line.Templates)
                {
                    if (template.Split < 0 || template.Split >= splits.Length)
                        continue;

                    // Preserve skipped columns, including stale converted templates.
                    if (textFile.SkipColumns.Contains(template.Split))
                        continue;

                    var fragments = line.Splits
                        .Where(s => s.Split == template.Split)
                        .OrderBy(s => s.SubIndex)
                        .ToList();

                    // A QcRejected outcome still packages the plain pre-QC reconstruction, so only a
                    // missing result fails the row.
                    var resolved = PackagingHelpers.ResolveFragments(fragments, template, textFile, qualityControl, anchorFallsBackToFirst: true);
                    if (resolved.Text == null)
                    {
                        failed = true;
                        break;
                    }

                    var rawText = resolved.QcAnchor?.Text ?? string.Concat(fragments.Select(f => f.Text));
                    splits[template.Split] = PackagingTextFixups.Apply(config, textFile, template.Split, rawText, resolved.Text);
                    onColumnPackaged?.Invoke(template.Split, rawText, splits[template.Split]);
                }

                if (!failed)
                {
                    foreach (var split in line.Splits.Where(s => !templatedColumns.Contains(s.Split)))
                    {
                        if (split.Split < 0 || split.Split >= splits.Length)
                            continue;

                        // Preserve skipped columns; a stale compound split must not overwrite the cell.
                        if (textFile.SkipColumns.Contains(split.Split))
                            continue;

                        if (!textFile.PackageOutput || split.FlaggedForRetranslation || !split.SafeToTranslate)
                        {
                            failed = true;
                            break;
                        }

                        // Flags were checked above, so a missing result here only means no usable
                        // translation - which leaves an empty-Text cell untouched rather than failing.
                        var resolved = PackagingHelpers.ResolvePlainSplit(split, qualityControl);

                        if (resolved.Text != null)
                        {
                            splits[split.Split] = PackagingTextFixups.Apply(config, textFile, split.Split, split.Text, resolved.Text);
                            onColumnPackaged?.Invoke(split.Split, split.Text, splits[split.Split]);
                        }
                        else if (!string.IsNullOrEmpty(split.Text))
                        {
                            failed = true;
                            break;
                        }
                    }
                }

                if (failed)
                {
                    outputLines.Add(line.Raw);
                    counts.RawFallback++;
                }
                else
                {
                    if (rowPostProcess != null)
                        splits = rowPostProcess(splits);

                    var rebuilt = CompoundFieldSplitter.RebuildCsvRow(splits);
                    outputLines.Add(rebuilt);
                    counts.Passed++;
                }
            }

            await Task.CompletedTask;
        });

        FileHelper.WriteAllLinesWithRetry($"{outputPath}/{textFile.Path}", outputLines);

        // CSV rows never fail purely for a low QC score - a template column below MinAcceptableScore
        // just skips its QcTranslated correction and falls through to the row's plain, pre-QC
        // fragment translation instead (see PackagingHelpers.ResolveFragments/ResolvePlainSplit), so
        // every failure counted here is a RawFallback.
        return counts.ToTuple();
    }
}
