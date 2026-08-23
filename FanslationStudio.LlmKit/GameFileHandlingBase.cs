using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit;

public static class GameFileHandlingBase
{
    public static string CalculateVersionNumber() => DateTime.Now.ToString("yyyy.MM.dd.HH.mm");

    public static void CopyDirectory(string sourceDir, string destDir, bool overwrite = false)
    {
        // Get the subdirectories for the specified directory.
        var dir = new DirectoryInfo(sourceDir);

        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory does not exist or could not be found: {sourceDir}");

        // If the destination directory doesn't exist, create it.
        if (!Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        // Get the files in the directory and copy them to the new location.
        FileInfo[] files = dir.GetFiles();
        foreach (FileInfo file in files)
        {
            var tempPath = Path.Combine(destDir, file.Name);
            file.CopyTo(tempPath, overwrite);
        }

        // Copy each subdirectory using recursion
        DirectoryInfo[] dirs = dir.GetDirectories();
        foreach (DirectoryInfo subdir in dirs)
        {
            if (subdir.Name == ".git" || subdir.Name == ".vs")
                continue;

            var tempPath = Path.Combine(destDir, subdir.Name);
            CopyDirectory(subdir.FullName, tempPath, overwrite);
        }
    }

    public static async Task MergeFilesIntoTranslatedAsync(string workingDirectory,
        TextFileToSplit[] textFiles)
    {
        await FileIteration.IterateTranslatedFilesAsync(workingDirectory, textFiles, async (outputFile, textFileToTranslate, fileLines) =>
        {
            var newCount = 0;

            ////Disable for now since they should be same
            //if (textFileToTranslate.TextFileType == TextFileType.RegularDb)
            //    return;

            var deserializer = YamlHelper.CreateDeserializer();
            var exportFile = outputFile.Replace("Converted", "Raw/Export");
            var exportLines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(exportFile));

            foreach (var line in exportLines)
            {
                var found = fileLines.FirstOrDefault(x => x.Raw == line.Raw);
                if (found != null)
                {
                    foreach (var split in line.Splits)
                    {
                        var found2 = found.Splits.FirstOrDefault(x => x.Split == split.Split && x.SubIndex == split.SubIndex && x.Text == split.Text)
                            ?? found.Splits.FirstOrDefault(x => x.Text == split.Text);

                        if (found2 != null)
                            split.Translated = found2.Translated;
                    }
                }
                else
                {
                    // Try matching on split instead of line incase they changed line format
                    foreach (var split in line.Splits)
                    {
                        var found2 = fileLines
                            .Select(x => x.Splits.FirstOrDefault(s => s.Split == split.Split && s.SubIndex == split.SubIndex && s.Text == split.Text))
                            .FirstOrDefault(s => s != null)
                            ?? fileLines
                                .Select(x => x.Splits.FirstOrDefault(s => s.Text == split.Text))
                                .FirstOrDefault(s => s != null);

                        if (found2 != null)
                            split.Translated = found2.Translated;
                        else
                            newCount++;
                    }
                }
            }

            Console.WriteLine($"New Lines {textFileToTranslate.Path}: {newCount}");

            //if (newCount > 0 || exportLines.Count != fileLines.Count) //Always Write because they might have changed format
            {
                var serializer = YamlHelper.CreateSerializer();
                File.WriteAllText(outputFile, serializer.Serialize(exportLines));
            }

            await Task.CompletedTask;
        });
    }

    public static List<string> CheckFileLinesMatch(string workingDirectory, TextFileToSplit[] textFiles)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory);
        var badFiles = new List<string>();

        foreach (var textFile in textFiles)
        {
            var file = $"{workingDirectory}/Raw/Export/{textFile.Path}.yaml";
            var convertedFile = $"{workingDirectory}/Converted/{textFile.Path}.yaml";

            var deserializer = YamlHelper.CreateDeserializer();

            var lines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(file));
            var convertedLines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(convertedFile)); ;

            if (lines.Count != convertedLines.Count)
                badFiles.Add($"Bad File: {Path.GetFileName(file)} Export: {lines.Count} Converted: {convertedLines.Count} ");
        }

        return badFiles;
    }
}
