using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Standard, game-agnostic handling for <see cref="TextFileType.PrefabText"/> files - hardcoded
/// UI/prefab text baked directly into MonoBehaviour/TMP_Text components rather than a game-data
/// CSV (see the consuming project's asset-dumping test, e.g. DragonHeirOverLlm's
/// AssetDumperWorkflowTests, which produces the plain "one distinct string per line" input file
/// this reads). Each line is decomposed via <see cref="CompoundFieldSplitter.Decompose"/> exactly
/// like a RegularDb CSV cell (the line is treated as the file's only "column", index 0) - a line
/// with a single Chinese run spanning its whole length still gets recorded as one plain whole-line
/// TranslationSplit with no template, but a line packing multiple Chinese runs together with
/// structural separators/placeholders gets a FieldTemplate + per-fragment TranslationSplits just
/// like a compound CSV column would.
/// </summary>
public static class PrefabTextWorkflow
{
    /// <summary>
    /// Reads a plain-text file (one distinct string per line, blank lines ignored) from
    /// Raw/Dumped/PrefabText/{textFile.Path} and produces the same TranslationLine YAML shape the
    /// CSV export path uses, so it flows through the existing Converted/merge/translate pipeline
    /// (GameFileHandlingBase.MergeFilesIntoTranslatedAsync, Workflow/TranslationWorkflow.cs, etc.)
    /// completely unchanged.
    ///
    /// Each line is run through <see cref="CompoundFieldSplitter.Decompose"/> exactly like a
    /// RegularDb CSV cell (treated as the line's only "column", index 0), rather than always being
    /// recorded as a single whole-line fragment. This means a PrefabText line that packs multiple
    /// Chinese runs together with structural separators/placeholders follows the exact same
    /// splitting rules (placeholder gluing via <paramref name="options"/>, digit/percent/CJK
    /// punctuation absorption, adjacent-fragment merging, etc.) as any other compound field, and a
    /// line that decomposes to nothing but a single whole-line fragment
    /// (<see cref="CompoundFieldSplitter.IsTrivialTemplate"/>) still gets recorded as a plain
    /// whole-line split with no template, same as a trivial CSV column - avoiding template noise
    /// for the common case.
    /// </summary>
    public static void ExportPrefabTextToCustomFormat(
        string workingDirectory, TextFileToSplit textFile, CompoundFieldSplitterOptions? options = null)
    {
        var dumpedPath = $"{workingDirectory}/Raw/Dumped/PrefabText/{textFile.Path}";
        var exportPath = $"{workingDirectory}/Raw/Export";
        var convertedPath = $"{workingDirectory}/Converted";

        Directory.CreateDirectory(exportPath);
        Directory.CreateDirectory(convertedPath);


        var foundLines = File.ReadAllLines(dumpedPath)
            .Where(line => !string.IsNullOrEmpty(line))
            .Select(line =>
            {
                var (template, fragments) = CompoundFieldSplitter.Decompose(line, options);

                if (fragments.Count == 0)
                {
                    // No Chinese text found - shouldn't normally happen for a dumped Chinese string,
                    // but fall back to the previous whole-line behavior rather than dropping the line.
                    return new TranslationLine
                    {
                        Raw = line,
                        Splits = [new TranslationSplit(0, 0, line)],
                    };
                }

                if (CompoundFieldSplitter.IsTrivialTemplate(template, fragments.Count))
                {
                    return new TranslationLine
                    {
                        Raw = line,
                        Splits = [new TranslationSplit(0, 0, fragments[0])],
                    };
                }

                return new TranslationLine
                {
                    Raw = line,
                    Templates = [new FieldTemplate(0, template)],
                    Splits = fragments.Select((fragment, index) => new TranslationSplit(0, index, fragment)).ToList(),
                };
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
    /// A line decomposed into a compound-field template (see <see cref="ExportPrefabTextToCustomFormat"/>)
    /// is rebuilt via <see cref="CompoundFieldSplitter.Reconstruct"/> from its translated fragments,
    /// exactly like a RegularDb CSV cell - if any fragment is untranslated, flagged for
    /// retranslation, or unsafe, the whole line falls back to its original raw text rather than
    /// reconstructing a partially-translated result (matching the CSV reconstruction path in
    /// GameFileHandling.PackageFinalTranslationAsync). A trivial (non-templated) line falls back to
    /// its single split's original text under the same conditions.
    /// </summary>
    public static async Task<(int Passed, int Failed)> PackagePrefabTextAsync(string workingDirectory, TextFileToSplit textFile)
    {
        var outputPath = $"{workingDirectory}/Mod";
        Directory.CreateDirectory(outputPath);

        var results = new List<PrefabTextResult>();
        var passedCount = 0;
        var failedCount = 0;

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory, [textFile], async (_, _, fileLines) =>
        {
            foreach (var line in fileLines)
            {
                var (result, failed) = ReconstructLine(line, textFile);
                if (result == null)
                    continue;

                // Undo hyphen
                result = result.Replace("\u2011", "-");

                results.Add(new PrefabTextResult(line.Raw, result));

                if (failed)
                    failedCount++;
                else
                    passedCount++;
            }

            await Task.CompletedTask;
        });

        var serializer = YamlHelper.CreateSerializer();
        await File.WriteAllTextAsync($"{outputPath}/{textFile.Path}.yaml", serializer.Serialize(results));

        return (passedCount, failedCount);
    }

    /// <summary>
    /// Reconstructs a single line's packaged output. The returned <c>Failed</c> flag reflects
    /// whether the line fell back to its original raw text because a fragment/split was
    /// unsafe, flagged for retranslation, or missing its translation - this must be reported by
    /// the caller as an actual failure (see <see cref="PackagePrefabTextAsync"/>) rather than
    /// silently folded into the same bucket as a genuinely successful line, since both cases
    /// otherwise look identical in the packaged raw/result YAML.
    /// </summary>
    private static (string? Result, bool Failed) ReconstructLine(TranslationLine line, TextFileToSplit textFile)
    {
        var template = line.Templates.FirstOrDefault(t => t.Split == 0);
        if (template != null)
        {
            var fragments = line.Splits.Where(s => s.Split == 0).OrderBy(s => s.SubIndex).ToList();
            var translatedFragments = new List<string>();

            foreach (var fragment in fragments)
            {
                if (!textFile.PackageOutput || fragment.FlaggedForRetranslation || !fragment.SafeToTranslate)
                    return (line.Raw, true);

                if (!string.IsNullOrEmpty(fragment.Translated))
                    translatedFragments.Add(fragment.Translated);
                else if (!string.IsNullOrEmpty(fragment.Text))
                    return (line.Raw, true);
                else
                    translatedFragments.Add(fragment.Text);
            }

            return (CompoundFieldSplitter.Reconstruct(template.Template, translatedFragments), false);
        }

        var split = line.Splits.FirstOrDefault(s => s.Split == 0);
        if (split == null)
            return (null, false);

        if (!string.IsNullOrEmpty(split.Translated) && !split.FlaggedForRetranslation && split.SafeToTranslate)
            return (split.Translated, false);

        return (split.Text, true);
    }
}
