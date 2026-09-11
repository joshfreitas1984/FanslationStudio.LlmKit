using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Text.RegularExpressions;

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
            //if (textFileToTranslate.TextFileType == TextFileType.RawCsv)
            //    return;

            var deserializer = YamlHelper.CreateDeserializer();
            var exportFile = outputFile.Replace("Converted", "Raw/Export");
            var exportLines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(exportFile));

            foreach (var line in exportLines)
            {
                // A JSON line's identity is its RawIndex (the object's "Key"), not its whole Raw
                // text - Raw legitimately changes whenever any untranslated field in the object
                // changes (e.g. a numeric stat updated by a game patch), which must not invalidate
                // already-translated fields. Every other file type has RawIndex == "" and keeps
                // matching by Raw equality exactly as before.
                var found = !string.IsNullOrEmpty(line.RawIndex)
                    ? fileLines.FirstOrDefault(x => x.RawIndex == line.RawIndex)
                    : fileLines.FirstOrDefault(x => x.Raw == line.Raw);

                if (found != null)
                {
                    foreach (var split in line.Splits)
                    {
                        var found2 = !string.IsNullOrEmpty(split.SplitPath)
                            ? found.Splits.FirstOrDefault(x => x.SplitPath == split.SplitPath && x.SubIndex == split.SubIndex && x.Text == split.Text)
                                ?? found.Splits.FirstOrDefault(x => x.SplitPath == split.SplitPath && x.Text == split.Text)
                            : found.Splits.FirstOrDefault(x => x.Split == split.Split && x.SubIndex == split.SubIndex && x.Text == split.Text)
                                ?? found.Splits.FirstOrDefault(x => x.Text == split.Text);

                        if (found2 != null)
                        {
                            split.Translated = found2.Translated;
                            CopyQcState(found2, split);
                        }
                    }
                }
                else
                {
                    // Try matching on split instead of line incase they changed line format
                    foreach (var split in line.Splits)
                    {
                        var found2 = !string.IsNullOrEmpty(split.SplitPath)
                            ? fileLines
                                .Select(x => x.Splits.FirstOrDefault(s => s.SplitPath == split.SplitPath && s.SubIndex == split.SubIndex && s.Text == split.Text))
                                .FirstOrDefault(s => s != null)
                                ?? fileLines
                                    .Select(x => x.Splits.FirstOrDefault(s => s.SplitPath == split.SplitPath && s.Text == split.Text))
                                    .FirstOrDefault(s => s != null)
                            : fileLines
                                .Select(x => x.Splits.FirstOrDefault(s => s.Split == split.Split && s.SubIndex == split.SubIndex && s.Text == split.Text))
                                .FirstOrDefault(s => s != null)
                                ?? fileLines
                                    .Select(x => x.Splits.FirstOrDefault(s => s.Text == split.Text))
                                    .FirstOrDefault(s => s != null);

                        if (found2 != null)
                        {
                            split.Translated = found2.Translated;
                            CopyQcState(found2, split);
                        }
                        else
                            newCount++;
                    }
                }
            }

            Console.WriteLine($"New Lines {textFileToTranslate.Path}: {newCount}");

            //if (newCount > 0 || exportLines.Count != fileLines.Count) //Always Write because they might have changed format
            {
                var serializer = YamlHelper.CreateSerializer();
                FileHelper.WriteAllTextWithRetry(outputFile, serializer.Serialize(exportLines));
            }

            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Carries a matched split's quality-review-pass state (see docs/plans/quality-review-pass.md)
    /// forward alongside its <see cref="TranslationSplit.Translated"/> value during a re-export
    /// merge. Both match paths in <see cref="MergeFilesIntoTranslatedAsync"/> require the split's
    /// <see cref="TranslationSplit.Text"/> to be identical between old and new before a match is
    /// even considered, so this is only ever called when the underlying raw fragment genuinely
    /// hasn't changed - safe to copy alongside Translated. Without this, every re-export/merge
    /// would silently reset every column's Qc* fields to "never reviewed" even for lines where
    /// nothing actually changed, forcing a full, expensive re-review of the entire corpus every
    /// time a game update is re-exported - defeating the entire point of
    /// <see cref="TranslationSplit.QcReviewedText"/>'s skip-if-unchanged check. This is purely a
    /// performance/cost concern, not a correctness one - packaging never trusts stale Qc* data
    /// regardless (see <see cref="Utility.QualityReviewHelpers.IsQcReviewFresh"/>), so even if this
    /// copy were skipped the worst outcome is an unnecessary re-review, never wrong output.
    /// </summary>
    private static void CopyQcState(TranslationSplit from, TranslationSplit to)
    {
        to.QcTranslated = from.QcTranslated;
        to.QcStatus = from.QcStatus;
        to.QcReviewedText = from.QcReviewedText;
        to.FlaggedForQcReview = from.FlaggedForQcReview;
        to.QcRejectedCorrection = from.QcRejectedCorrection;
        to.QcFailureReason = from.QcFailureReason;
        to.QcQualityScore = from.QcQualityScore;
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

    public static TextFileToSplit DefaultTestTextFile() => new TextFileToSplit()
    {
        Path = "",
    };

    public record FailedTranslation(string Text, string Translated, string Reason);

    public static async Task<(List<FailedTranslation> failures, List<string> forTheGlossary)> GetFailedTranslations(
        string workingDirectory, TextFileToSplit[] textFiles)
    {
        var failures = new List<FailedTranslation>();
        var pattern = LineValidation.ChineseCharPattern;

        var forTheGlossary = new List<string>();

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory,
            textFiles,
            async (outputFile, textFileToTranslate, fileLines) =>
            {
                foreach (var line in fileLines)
                {
                    foreach (var split in line.Splits)
                    {
                        if (string.IsNullOrEmpty(split.Text))
                            continue;

                        // If it is already translated or just special characters return it
                        if (!Regex.IsMatch(split.Text, pattern))
                            continue;

                        if (!string.IsNullOrEmpty(split.Text) && (string.IsNullOrEmpty(split.Translated) || split.FlaggedForRetranslation))
                        {
                            failures.Add(new FailedTranslation(split.Text, split.Translated, split.FlaggedMistranslation));

                            if (split.Text.Length < 6)
                                if (!forTheGlossary.Contains(split.Text))
                                    forTheGlossary.Add(split.Text);
                        }
                    }
                }

                await Task.CompletedTask;
            });
        return (failures, forTheGlossary);
    }
}
