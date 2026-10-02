using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

public class FileIteration
{
    public static async Task IterateTranslatedFilesAsync(string workingDirectory,
        TextFileToSplit[] textFiles,
        Func<string, TextFileToSplit, List<TranslationLine>, Task> performActionAsync)
    {
        var deserializer = YamlHelper.CreateDeserializer();
        string outputPath = $"{workingDirectory}/Converted";

        foreach (var textFileToTranslate in textFiles)
        {
            var outputFile = $"{outputPath}/{textFileToTranslate.Path}.yaml";

            if (!File.Exists(outputFile))
                continue;

            var content = await File.ReadAllTextAsync(outputFile);

            var fileLines = deserializer.Deserialize<List<TranslationLine>>(content);

            if (performActionAsync != null)
                await performActionAsync(outputFile, textFileToTranslate, fileLines);
        }
    }

    /// <summary>
    /// Upper bound on files loaded/processed at once - every file's whole deserialized line list
    /// is held in memory while its action runs, so an unbounded fan-out held the entire corpus at
    /// once on large projects.
    /// </summary>
    public static int MaxParallelFiles { get; set; } = Math.Max(2, Environment.ProcessorCount);

    public static async Task IterateTranslatedFilesInParallelAsync(string workingDirectory,
        TextFileToSplit[] textFiles,
        Func<string, TextFileToSplit, List<TranslationLine>, Task> performActionAsync)
    {
        var deserializer = YamlHelper.CreateDeserializer();
        string outputPath = $"{workingDirectory}/Converted";

        await Parallel.ForEachAsync(textFiles,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFiles },
            async (textFileToTranslate, _) =>
            {
                var outputFile = $"{outputPath}/{textFileToTranslate.Path}.yaml";

                if (!File.Exists(outputFile))
                    return;

                var content = await File.ReadAllTextAsync(outputFile);
                var fileLines = deserializer.Deserialize<List<TranslationLine>>(content);

                if (performActionAsync != null)
                    await performActionAsync(outputFile, textFileToTranslate, fileLines);
            });
    }
}
