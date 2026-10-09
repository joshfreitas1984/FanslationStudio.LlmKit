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
            //if (textFileToTranslate.TextFileType == TextFileType.RawCsv)
            //    return;

            var deserializer = YamlHelper.CreateDeserializer();
            var exportFile = outputFile.Replace("Converted", "Raw/Export");
            var exportLines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(exportFile));

            var index = new ConvertedLineIndex(fileLines);

            foreach (var line in exportLines)
            {
                // A JSON line's identity is its RawIndex (the object's "Key"), not its whole Raw
                // text - Raw legitimately changes whenever any untranslated field in the object
                // changes (e.g. a numeric stat updated by a game patch), which must not invalidate
                // already-translated fields. Every other file type has RawIndex == "" and keeps
                // matching by Raw equality exactly as before.
                var found = !string.IsNullOrEmpty(line.RawIndex)
                    ? index.FindByRawIndex(line.RawIndex)
                    : index.FindByRaw(line.Raw);

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
                        var found2 = index.FindSplit(split);

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
    /// Lookups over one converted file's lines for <see cref="MergeFilesIntoTranslatedAsync"/>, built
    /// on first use. Every key keeps its first occurrence in file order (lines, then splits), so each
    /// lookup returns exactly what a FirstOrDefault scan over the same lines would.
    /// </summary>
    private sealed class ConvertedLineIndex(List<TranslationLine> lines)
    {
        private Dictionary<string, TranslationLine>? _byRawIndex;
        private Dictionary<string, TranslationLine>? _byRaw;
        private TranslationLine? _firstNullRaw;

        private Dictionary<(string? SplitPath, int SubIndex, string? Text), TranslationSplit>? _byPathSubIndexText;
        private Dictionary<(string? SplitPath, string? Text), TranslationSplit>? _byPathText;
        private Dictionary<(int Split, int SubIndex, string? Text), TranslationSplit>? _bySplitSubIndexText;
        private Dictionary<ValueTuple<string?>, TranslationSplit>? _byText;

        public TranslationLine? FindByRawIndex(string rawIndex)
        {
            if (_byRawIndex == null)
            {
                _byRawIndex = [];
                foreach (var line in lines)
                    if (line.RawIndex != null)
                        _byRawIndex.TryAdd(line.RawIndex, line);
            }

            return _byRawIndex.GetValueOrDefault(rawIndex);
        }

        public TranslationLine? FindByRaw(string? raw)
        {
            if (_byRaw == null)
            {
                _byRaw = [];
                foreach (var line in lines)
                {
                    if (line.Raw != null)
                        _byRaw.TryAdd(line.Raw, line);
                    else
                        _firstNullRaw ??= line;
                }
            }

            return raw == null ? _firstNullRaw : _byRaw.GetValueOrDefault(raw);
        }

        /// <summary>
        /// Split-level match across every line, used when the export line itself has no converted
        /// counterpart (e.g. its row format changed). A SplitPath split matches on
        /// (SplitPath, SubIndex, Text) then (SplitPath, Text); any other split on
        /// (Split, SubIndex, Text) then Text alone.
        /// </summary>
        public TranslationSplit? FindSplit(TranslationSplit split)
        {
            if (_byText == null)
                BuildSplitIndexes();

            return !string.IsNullOrEmpty(split.SplitPath)
                ? _byPathSubIndexText!.GetValueOrDefault((split.SplitPath, split.SubIndex, split.Text))
                    ?? _byPathText!.GetValueOrDefault((split.SplitPath, split.Text))
                : _bySplitSubIndexText!.GetValueOrDefault((split.Split, split.SubIndex, split.Text))
                    ?? _byText!.GetValueOrDefault(new ValueTuple<string?>(split.Text));
        }

        private void BuildSplitIndexes()
        {
            _byPathSubIndexText = [];
            _byPathText = [];
            _bySplitSubIndexText = [];
            _byText = [];

            foreach (var line in lines)
            {
                foreach (var split in line.Splits)
                {
                    _byPathSubIndexText.TryAdd((split.SplitPath, split.SubIndex, split.Text), split);
                    _byPathText.TryAdd((split.SplitPath, split.Text), split);
                    _bySplitSubIndexText.TryAdd((split.Split, split.SubIndex, split.Text), split);
                    _byText.TryAdd(new ValueTuple<string?>(split.Text), split);
                }
            }
        }
    }

    /// <summary>
    /// Carries a matched split's quality-control-pass state (see docs/plans/quality-control-pass.md)
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
    /// regardless (see <see cref="Utility.QualityControlHelpers.IsQcReviewFresh"/>), so even if this
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
        to.QcDefectCategory = from.QcDefectCategory;
        to.QcDefectCategories = [.. from.QcDefectCategories];
        // Must survive re-export like it survives ResetQcState, or the retry limit restarts from 0
        // after every merge. Its baseline check still invalidates it on any upstream change.
        to.QcRuleCheckFailureCount = from.QcRuleCheckFailureCount;
        to.QcRuleCheckFailureBaseline = from.QcRuleCheckFailureBaseline;
    }

    public static List<string> CheckFileLinesMatch(string workingDirectory, TextFileToSplit[] textFiles)
    {
        var badFiles = new List<string>();

        foreach (var textFile in textFiles)
        {
            var file = $"{workingDirectory}/Raw/Export/{textFile.Path}.yaml";
            var convertedFile = $"{workingDirectory}/Converted/{textFile.Path}.yaml";

            var deserializer = YamlHelper.CreateDeserializer();

            var lines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(file));
            var convertedLines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(convertedFile));

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

        var forTheGlossary = new List<string>();
        var forTheGlossarySeen = new HashSet<string>();

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
                        if (!LineValidation.ContainsCjk(split.Text))
                            continue;

                        if (!string.IsNullOrEmpty(split.Text) && (string.IsNullOrEmpty(split.Translated) || split.FlaggedForRetranslation))
                        {
                            failures.Add(new FailedTranslation(split.Text, split.Translated, split.FlaggedMistranslation));

                            if (split.Text.Length < 6 && forTheGlossarySeen.Add(split.Text))
                                forTheGlossary.Add(split.Text);
                        }
                    }
                }

                await Task.CompletedTask;
            });
        return (failures, forTheGlossary);
    }
}
