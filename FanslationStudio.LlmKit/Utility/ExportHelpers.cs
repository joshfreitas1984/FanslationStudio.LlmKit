using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Raw/Export side shared by every per-file export workflow (Csv/Json/PrefabText/DynamicStrings).
/// </summary>
internal static class ExportHelpers
{
    /// <summary>
    /// Writes <paramref name="lines"/> to Raw/Export/{textFile.Path}.yaml and seeds
    /// Converted/{textFile.Path}.yaml from it only if that file doesn't exist yet - an
    /// already-accumulated Converted file is never overwritten.
    /// </summary>
    /// <summary>
    /// Makes sure <c>namedTokenReplacers</c> is configured before a file is decomposed: a process that runs only an export has not
    /// loaded the configuration yet, and the tokens would silently stay unslotted. Does nothing once any configuration has been loaded,
    /// or when the working directory has no Config.yaml.
    /// </summary>
    public static void EnsureTokensConfigured(string workingDirectory)
    {
        if (!StringTokenReplacer.IsConfigured && File.Exists($"{workingDirectory}/Config.yaml"))
            Configuration.ConfigurationExtensions.GetConfiguration(workingDirectory);
    }

    public static void WriteExport(string workingDirectory, TextFileToSplit textFile, List<TranslationLine> lines)
    {
        var exportPath = $"{workingDirectory}/Raw/Export";
        var convertedPath = $"{workingDirectory}/Converted";

        Directory.CreateDirectory(exportPath);
        Directory.CreateDirectory(convertedPath);

        var exportFile = $"{exportPath}/{textFile.Path}.yaml";
        var convertedFile = $"{convertedPath}/{textFile.Path}.yaml";

        FileHelper.WriteAllTextWithRetry(exportFile, YamlHelper.CreateSerializer().Serialize(lines));

        if (!File.Exists(convertedFile))
            File.Copy(exportFile, convertedFile);
    }

    /// <summary>
    /// Decomposes one flat-file string (treated as its only "column", index 0) into a
    /// <see cref="TranslationLine"/>: a plain whole-line split for a trivial template, a
    /// <see cref="FieldTemplate"/> plus per-fragment splits for a compound one, and the whole line as
    /// a single split when no fragment is found at all (rather than dropping the line).
    /// </summary>
    public static TranslationLine DecomposeFlatLine(string line, CompoundFieldSplitterOptions? options, bool enableSizeShrink)
    {
        var (template, fragments, tokens, templateTokens) = CompoundFieldSplitter.DecomposeSlotted(line, options, enableSizeShrink);

        if (fragments.Count == 0)
        {
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
                Splits = [new TranslationSplit(0, 0, fragments[0]) { Tokens = tokens[0] }],
            };
        }

        return new TranslationLine
        {
            Raw = line,
            Templates = [new FieldTemplate(0, template) { Tokens = templateTokens }],
            Splits = fragments.Select((fragment, index) => new TranslationSplit(0, index, fragment) { Tokens = tokens[index] }).ToList(),
        };
    }
}
