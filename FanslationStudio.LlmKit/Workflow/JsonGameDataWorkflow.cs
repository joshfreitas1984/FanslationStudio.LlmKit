using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Handling for <see cref="TextFileType.RawJson"/> files - JSON arrays of objects, one object per
/// row, keyed by a "Key" property, with a loose/variable per-object schema (not every object has
/// every property). Translatable fields are addressed by JSON property path
/// (<see cref="TranslationSplit.SplitPath"/>/<see cref="FieldTemplate.SplitPath"/>, e.g. "Desc" or
/// "SomeArray[2]") rather than column index, and a line's identity for re-export matching is its
/// "Key" (<see cref="TranslationLine.RawIndex"/>) rather than its whole <see cref="TranslationLine.Raw"/>
/// text - see <see cref="GameFileHandlingBase.MergeFilesIntoTranslatedAsync"/>. Mirrors
/// <see cref="CsvGameDataWorkflow"/>'s per-file API shape and Raw/Export -> Converted -> Mod flow,
/// with property-path addressing and per-field (not whole-row) failure handling in place of
/// column-index addressing and whole-row fallback, since one JSON object commonly holds many
/// independent translatable fields where one failing must not discard the rest.
/// </summary>
public static class JsonGameDataWorkflow
{
    private static readonly Regex ArrayIndexSuffix = new(@"^(.+)\[(\d+)\]$", RegexOptions.Compiled);

    /// <summary>
    /// Reads a dumped JSON array file from <paramref name="rawSubfolder"/>/{textFile.Path} (default
    /// "Raw/Dumped", matching this game's own dump convention), decomposes every translatable
    /// string/string-array-element property via <see cref="CompoundFieldSplitter.Decompose"/>, and
    /// writes the same TranslationLine YAML shape <see cref="CsvGameDataWorkflow"/> uses into
    /// Raw/Export and (if not already present) Converted.
    /// </summary>
    public static void ExportToCustomFormat(
        string workingDirectory, TextFileToSplit textFile, CompoundFieldSplitterOptions? options = null,
        string rawSubfolder = "Raw/Dumped")
    {
        var dumpedPath = $"{workingDirectory}/{rawSubfolder}/{textFile.Path}";
        var exportPath = $"{workingDirectory}/Raw/Export";
        var convertedPath = $"{workingDirectory}/Converted";

        Directory.CreateDirectory(exportPath);
        Directory.CreateDirectory(convertedPath);

        using var jsonDoc = JsonDocument.Parse(File.ReadAllText(dumpedPath));
        var foundLines = new List<TranslationLine>();

        if (jsonDoc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in jsonDoc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("Key", out var keyElement))
                    continue;

                var line = new TranslationLine
                {
                    Raw = entry.GetRawText(),
                    RawIndex = keyElement.ToString(),
                };

                foreach (var property in entry.EnumerateObject())
                {
                    if (property.Name == "Key")
                        continue;

                    if (IsSkippedSiblingProperty(property.Name))
                        continue;

                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        DecomposeInto(line, property.Name, property.Value.GetString(), options, textFile.EnableSizeShrink);
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        var index = 0;
                        foreach (var element in property.Value.EnumerateArray())
                        {
                            if (element.ValueKind == JsonValueKind.String)
                                DecomposeInto(line, $"{property.Name}[{index}]", element.GetString(), options, textFile.EnableSizeShrink);

                            index++;
                        }
                    }
                }

                if (line.Splits.Count > 0)
                    foundLines.Add(line);
            }
        }

        var serializer = YamlHelper.CreateSerializer();
        FileHelper.WriteAllTextWithRetry($"{exportPath}/{textFile.Path}.yaml", serializer.Serialize(foundLines));

        // Never overwrite an already-accumulated Converted/*.yaml - matches Csv/Prefab/DynamicStrings.
        if (!File.Exists($"{convertedPath}/{textFile.Path}.yaml"))
            File.Copy($"{exportPath}/{textFile.Path}.yaml", $"{convertedPath}/{textFile.Path}.yaml");
    }

    /// <summary>
    /// A parallel "...Tw" (Traditional Chinese) sibling property, or a "...Final" sibling, of a
    /// translatable field - this game's own data convention, not a general JSON one. Matches the
    /// original hand-rolled exporter's check exactly: "list" is stripped from anywhere in the
    /// (lowercased) name before checking the suffix, so "NameListTw" -> "nametw" -> ends with "tw".
    /// </summary>
    private static bool IsSkippedSiblingProperty(string propertyName)
    {
        var normalized = propertyName.ToLowerInvariant().Replace("list", "");
        return normalized.EndsWith("tw") || normalized.EndsWith("final");
    }

    private static void DecomposeInto(TranslationLine line, string splitPath, string? text, CompoundFieldSplitterOptions? options, bool enableSizeShrink)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var (template, fragments) = CompoundFieldSplitter.Decompose(text, options, enableSizeShrink);
        if (fragments.Count == 0)
            return;

        if (CompoundFieldSplitter.IsTrivialTemplate(template, fragments.Count))
        {
            line.Splits.Add(new TranslationSplit(0, 0, fragments[0]) { SplitPath = splitPath });
            return;
        }

        line.Templates.Add(new FieldTemplate(0, template) { SplitPath = splitPath });

        for (int f = 0; f < fragments.Count; f++)
            line.Splits.Add(new TranslationSplit(0, f, fragments[f]) { SplitPath = splitPath });
    }

    /// <summary>
    /// Packages a translated JSON file into Mod/{textFile.Path} (final JSON, no ".yaml" suffix -
    /// mirrors <see cref="CsvGameDataWorkflow.PackageAsync"/>'s "packaged output is already the
    /// game-consumable format" convention). Re-parses each line's <see cref="TranslationLine.Raw"/>
    /// to recover the exact original object shape (needed for any <see cref="TranslationSplit.SplitPath"/>
    /// ending in "[n]" to know the surrounding array's other, untouched elements), sets the "Key"
    /// property from <see cref="TranslationLine.RawIndex"/>, reconstructs/writes each translated
    /// property or array element at its path, and leaves everything else in the object (properties
    /// with no splits, "...Tw"/"...Final" siblings, non-string/non-array properties) copied through
    /// from the original parse untouched. Unlike <see cref="CsvGameDataWorkflow"/>, a failure is
    /// scoped to the single field it belongs to (falls back to that field's original raw text) -
    /// not the whole object - since one JSON object commonly holds many independent translatable
    /// fields where one failing must not discard translations already accepted for the rest.
    /// </summary>
    public static async Task<(int Passed, int Failed)> PackageAsync(string workingDirectory, TextFileToSplit textFile)
    {
        var outputPath = $"{workingDirectory}/Mod";
        Directory.CreateDirectory(outputPath);

        var minAcceptableScore = ConfigurationExtensions.GetConfiguration(workingDirectory).QualityReview.MinAcceptableScore;

        var outputArray = new JsonArray();
        var passedCount = 0;
        var failedCount = 0;

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory, [textFile], async (_, _, fileLines) =>
        {
            foreach (var line in fileLines)
            {
                var root = JsonNode.Parse(line.Raw)!.AsObject();

                if (int.TryParse(line.RawIndex, out var keyInt))
                    root["Key"] = keyInt;
                else
                    root["Key"] = line.RawIndex;

                var templatesByPath = line.Templates.ToDictionary(t => t.SplitPath);

                foreach (var group in line.Splits.GroupBy(s => s.SplitPath))
                {
                    var splitPath = group.Key;
                    var fragments = group.OrderBy(s => s.SubIndex).ToList();
                    var template = templatesByPath.GetValueOrDefault(splitPath);

                    var (ok, packagedText) = PackageField(fragments, template, textFile, minAcceptableScore);

                    if (!ok)
                    {
                        failedCount++;
                        continue; // leave the field at its original (already-parsed) raw value
                    }

                    SetValueAtPath(root, splitPath, packagedText);
                    passedCount++;
                }

                outputArray.Add(root);
            }

            await Task.CompletedTask;
        });

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        FileHelper.WriteAllTextWithRetry($"{outputPath}/{textFile.Path}", outputArray.ToJsonString(jsonOptions));

        return (passedCount, failedCount);
    }

    private static (bool Ok, string Text) PackageField(
        List<TranslationSplit> fragments, FieldTemplate? template, TextFileToSplit textFile, int minAcceptableScore)
    {
        var anchor = fragments.FirstOrDefault(f => f.SubIndex == 0) ?? fragments.FirstOrDefault();
        if (anchor == null)
            return (false, string.Empty);

        var qcFresh = QualityReviewHelpers.IsQcReviewFresh(anchor, template, fragments);
        var useQcTranslated = qcFresh
            && !string.IsNullOrEmpty(anchor.QcTranslated)
            && !(anchor.QcQualityScore is int score && score < minAcceptableScore);

        if (useQcTranslated)
            return (true, anchor.QcTranslated);

        var translatedFragments = new List<string>();

        foreach (var fragment in fragments)
        {
            if (!textFile.PackageOutput || fragment.FlaggedForRetranslation || !fragment.SafeToTranslate)
                return (false, string.Empty);

            if (!string.IsNullOrEmpty(fragment.Translated))
                translatedFragments.Add(fragment.Translated);
            else if (!string.IsNullOrEmpty(fragment.Text))
                return (false, string.Empty);
            else
                translatedFragments.Add(fragment.Text);
        }

        var packaged = template != null
            ? CompoundFieldSplitter.Reconstruct(template.Template, translatedFragments)
            : translatedFragments[0];

        return (true, packaged);
    }

    private static void SetValueAtPath(JsonObject root, string splitPath, string text)
    {
        var arrayMatch = ArrayIndexSuffix.Match(splitPath);
        if (!arrayMatch.Success)
        {
            root[splitPath] = text;
            return;
        }

        var propertyName = arrayMatch.Groups[1].Value;
        var index = int.Parse(arrayMatch.Groups[2].Value);

        if (root[propertyName] is JsonArray array && index >= 0 && index < array.Count)
            array[index] = text;
    }
}
