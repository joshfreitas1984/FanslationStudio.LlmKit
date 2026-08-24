using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Standard, game-agnostic handling for <see cref="TextFileType.PrefabText"/> files - hardcoded
/// UI/prefab text baked directly into MonoBehaviour/TMP_Text components rather than a game-data
/// CSV (see the consuming project's asset-dumping test, e.g. DragonHeirOverLlm's
/// AssetDumperWorkflowTests, which produces the plain "one distinct string per line" input file
/// this reads). Unlike RegularDb files (CSV rows decomposed into per-column fragments via
/// CompoundFieldSplitter), a dumped prefab-text file has no row/column structure - each line IS
/// the whole translatable unit, so it gets exactly one whole-line TranslationSplit and never a
/// FieldTemplate.
/// </summary>
public static class PrefabTextWorkflow
{
    /// <summary>
    /// Reads a plain-text file (one distinct string per line, blank lines ignored) from
    /// Raw/Dumped/PrefabText/{textFile.Path} and produces the same TranslationLine YAML shape the
    /// CSV export path uses, so it flows through the existing Converted/merge/translate pipeline
    /// (GameFileHandlingBase.MergeFilesIntoTranslatedAsync, Workflow/TranslationWorkflow.cs, etc.)
    /// completely unchanged.
    /// </summary>
    public static void ExportPrefabTextToCustomFormat(string workingDirectory, TextFileToSplit textFile)
    {
        var dumpedPath = $"{workingDirectory}/Raw/Dumped/PrefabText/{textFile.Path}";
        var exportPath = $"{workingDirectory}/Raw/Export";
        var convertedPath = $"{workingDirectory}/Converted";

        Directory.CreateDirectory(exportPath);
        Directory.CreateDirectory(convertedPath);

        var foundLines = File.ReadAllLines(dumpedPath)
            .Where(line => !string.IsNullOrEmpty(line))
            .Select(line => new TranslationLine
            {
                Raw = line,
                Splits = [new TranslationSplit(0, 0, line)],
            })
            .ToList();

        var serializer = YamlHelper.CreateSerializer();
        var yaml = serializer.Serialize(foundLines);
        File.WriteAllText($"{exportPath}/{textFile.Path}.yaml", yaml);

        // Add missing converted file if it doesn't exist yet - matches the CSV export path's
        // behavior of never overwriting an already-accumulated Converted/*.yaml.
        if (!File.Exists($"{convertedPath}/{textFile.Path}.yaml"))
            File.Copy($"{exportPath}/{textFile.Path}.yaml", $"{convertedPath}/{textFile.Path}.yaml");
    }

    /// <summary>
    /// Packages a translated PrefabText file into the flat raw/result YAML shape a runtime plugin
    /// can look up by exact raw-string match:
    /// <code>
    /// - raw: 地图一览
    ///   result: Map Overview
    /// </code>
    /// A line with no usable translation (untranslated, flagged for retranslation, or unsafe)
    /// falls back to its original text so the output always has an entry for every dumped string.
    /// </summary>
    public static async Task PackagePrefabTextAsync(string workingDirectory, TextFileToSplit textFile)
    {
        var outputPath = $"{workingDirectory}/Mod";
        Directory.CreateDirectory(outputPath);

        var results = new List<PrefabTextResult>();

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory, [textFile], async (_, _, fileLines) =>
        {
            foreach (var line in fileLines)
            {
                var split = line.Splits.FirstOrDefault();
                if (split == null)
                    continue;

                var result = !string.IsNullOrEmpty(split.Translated) && !split.FlaggedForRetranslation && split.SafeToTranslate
                    ? split.Translated
                    : split.Text;

                results.Add(new PrefabTextResult(line.Raw, result));
            }

            await Task.CompletedTask;
        });

        var serializer = YamlHelper.CreateSerializer();
        await File.WriteAllTextAsync($"{outputPath}/{textFile.Path}.yaml", serializer.Serialize(results));
    }
}
