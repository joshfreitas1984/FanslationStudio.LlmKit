using System.Text.RegularExpressions;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using SharedAssembly.DynamicStrings;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Standard, game-agnostic handling for <see cref="TextFileType.DynamicStrings"/> files - the
/// older Mono/Cecil-transpiler dump+patch format for hardcoded, runtime-assembled string literals,
/// distinct from <see cref="TextFileType.DynamicStringsIL2CPP"/> (see that value's XML doc and
/// <see cref="DynamicStringWorkflow"/>, which stays IL2CPP-only). A dumped line is 5 naive
/// (non-quote-aware) comma-separated fields - <c>Type,Method,ILOffset,RawText,ParamTypesList</c> -
/// with the dumper substituting full-width "，" for any literal comma that would otherwise appear
/// inside <c>RawText</c>/<c>ParamTypesList</c> (reversed at packaging time by
/// <see cref="DynamicStringSupport.PrepareMethodParameters"/>). The packaged output keys each
/// translated line by <c>Type</c>/<c>Method</c>/<c>ILOffset</c> (<see cref="DynamicStringContract"/>)
/// for a runtime Cecil-transpiler patch to find - a fundamentally different consumption model than
/// <see cref="DynamicStringWorkflow"/>'s flat substring-replacement dictionary, which is why this is
/// its own workflow rather than a variant of that one.
/// </summary>
public static class DynamicStringsCecilWorkflow
{
    /// <summary>
    /// Reads a dumped dynamicStrings file from <paramref name="rawSubfolder"/>/{textFile.Path}
    /// (default "Raw/Dumped", matching the original convention this generalizes), naive-splits each
    /// line on ',', and records each of the 5 positional fields as its own plain (non-decomposed)
    /// <see cref="TranslationSplit"/> when it matches the Chinese-character pattern - in practice
    /// only <c>RawText</c> (field index 3) ever realistically matches. Surrounding quote characters
    /// left over from the naive split are stripped from the recorded <see cref="TranslationSplit.Text"/>.
    /// Writes the standard TranslationLine YAML shape into Raw/Export and (if not already present)
    /// Converted, exactly like <see cref="PrefabTextWorkflow"/>/<see cref="CsvGameDataWorkflow"/>.
    /// </summary>
    public static void ExportDynamicStringsToCustomFormat(
        string workingDirectory, TextFileToSplit textFile, string rawSubfolder = "Raw/Dumped")
    {
        var dumpedPath = $"{workingDirectory}/{rawSubfolder}/{textFile.Path}";
        var exportPath = $"{workingDirectory}/Raw/Export";
        var convertedPath = $"{workingDirectory}/Converted";

        Directory.CreateDirectory(exportPath);
        Directory.CreateDirectory(convertedPath);

        var pattern = LineValidation.ChineseCharPattern;
        var foundLines = new List<TranslationLine>();

        foreach (var line in File.ReadAllLines(dumpedPath))
        {
            var splits = line.Split(",");
            var foundSplits = new List<TranslationSplit>();

            for (var i = 0; i < splits.Length; i++)
            {
                if (!Regex.IsMatch(splits[i], pattern))
                    continue;

                var cleaned = splits[i];
                if (cleaned.StartsWith('\"'))
                    cleaned = cleaned[1..];
                if (cleaned.EndsWith('\"'))
                    cleaned = cleaned[..^1];

                foundSplits.Add(new TranslationSplit(i, cleaned));
            }

            foundLines.Add(new TranslationLine
            {
                Raw = line,
                Splits = foundSplits,
            });
        }

        var serializer = YamlHelper.CreateSerializer();
        var yaml = serializer.Serialize(foundLines);
        FileHelper.WriteAllTextWithRetry($"{exportPath}/{textFile.Path}.yaml", yaml);

        // Never overwrite an already-accumulated Converted/*.yaml - matches Prefab/DynamicStringsIL2CPP.
        if (!File.Exists($"{convertedPath}/{textFile.Path}.yaml"))
            File.Copy($"{exportPath}/{textFile.Path}.yaml", $"{convertedPath}/{textFile.Path}.yaml");
    }

    /// <summary>
    /// Packages a translated dynamicStrings file into a <see cref="DynamicStringContract"/> list,
    /// keyed by <c>Type</c>/<c>Method</c>/<c>ILOffset</c> for the runtime Cecil-transpiler patch to
    /// find. Each line's <see cref="TranslationLine.Raw"/> is re-split on ',' (must yield exactly 5
    /// fields); the single translated split's full-width "，" is restored to a literal comma, field 4
    /// (ParamTypesList) is parsed via <see cref="DynamicStringSupport.PrepareMethodParameters"/>, and
    /// the resulting contract is kept only if <see cref="DynamicStringSupport.IsSafeContract"/>
    /// accepts it (game-specific type/method skip lists - see that method's doc comment). A line
    /// that doesn't have exactly one split, isn't safe to translate, doesn't re-split into 5 fields,
    /// is flagged for retranslation, or has no translation is skipped and counted as failed (unless
    /// unsafe-to-translate, which is not a failure - matches the original per-repo behavior this
    /// generalizes).
    /// </summary>
    public static async Task<(int Passed, int QcRejected, int RawFallback)> PackageDynamicStringsCecilAsync(string workingDirectory, TextFileToSplit textFile)
    {
        var outputPath = $"{workingDirectory}/Mod";
        Directory.CreateDirectory(outputPath);

        var contracts = new List<DynamicStringContract>();
        var passedCount = 0;
        var rawFallbackCount = 0;

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory, [textFile], async (_, _, fileLines) =>
        {
            foreach (var line in fileLines)
            {
                if (line.Splits.Count != 1)
                {
                    rawFallbackCount++;
                    continue;
                }

                // Do not package but don't count as failure.
                if (!line.Splits[0].SafeToTranslate)
                    continue;

                var splits = line.Raw.Split(",");
                var translated = line.Splits[0].Translated.Replace("，", ",");

                if (splits.Length != 5
                    || string.IsNullOrEmpty(translated)
                    || line.Splits[0].FlaggedForRetranslation)
                {
                    rawFallbackCount++;
                    continue;
                }

                var contract = new DynamicStringContract
                {
                    Type = splits[0],
                    Method = splits[1],
                    ILOffset = long.Parse(splits[2]),
                    Raw = splits[3],
                    Translation = translated,
                    Parameters = DynamicStringSupport.PrepareMethodParameters(splits[4]),
                };

                if (DynamicStringSupport.IsSafeContract(contract, false))
                {
                    contracts.Add(contract);
                    passedCount++;
                }
            }

            await Task.CompletedTask;
        });

        var serializer = YamlHelper.CreateSerializer();
        await FileHelper.WriteAllTextWithRetryAsync($"{outputPath}/{textFile.Path}.yaml", serializer.Serialize(contracts));

        // This legacy Mono/Cecil format has no quality-review integration - every failure counted
        // here is a RawFallback (a QcRejected count is never produced by this workflow).
        return (passedCount, 0, rawFallbackCount);
    }
}
