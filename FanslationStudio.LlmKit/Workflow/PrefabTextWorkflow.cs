using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Standard, game-agnostic handling for <see cref="TextFileType.PrefabText"/> files - hardcoded
/// UI/prefab text baked directly into MonoBehaviour/TMP_Text components rather than a game-data
/// CSV (see the consuming project's asset-dumping test, e.g. DragonHeirOverLlm's
/// AssetDumperWorkflowTests, which produces the plain "one distinct string per line" input file
/// this reads). Each line is decomposed via <see cref="CompoundFieldSplitter.Decompose"/> exactly
/// like a RawCsv cell (the line is treated as the file's only "column", index 0) - a line
/// with a single Chinese run spanning its whole length still gets recorded as one plain whole-line
/// TranslationSplit with no template, but a line packing multiple Chinese runs together with
/// structural separators/placeholders gets a FieldTemplate + per-fragment TranslationSplits just
/// like a compound CSV column would.
/// </summary>
public static class PrefabTextWorkflow
{
    /// <summary>
    /// Reads a plain-text file (one distinct string per line, blank lines ignored) from
    /// <paramref name="rawSubfolder"/>/{textFile.Path} (default "Raw/Dumped/PrefabText", matching
    /// the original convention this generalizes - see <see cref="CsvGameDataWorkflow.ExportToCustomFormat"/>'s
    /// own rawSubfolder parameter) and produces the same TranslationLine YAML shape the CSV export
    /// path uses, so it flows through the existing Converted/merge/translate pipeline
    /// (GameFileHandlingBase.MergeFilesIntoTranslatedAsync, Workflow/TranslationWorkflow.cs, etc.)
    /// completely unchanged.
    ///
    /// Each line is run through <see cref="CompoundFieldSplitter.Decompose"/> exactly like a
    /// RawCsv cell (treated as the line's only "column", index 0), rather than always being
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
        string workingDirectory, TextFileToSplit textFile, CompoundFieldSplitterOptions? options = null,
        string rawSubfolder = "Raw/Dumped/PrefabText")
    {
        var dumpedPath = $"{workingDirectory}/{rawSubfolder}/{textFile.Path}";

        var foundLines = File.ReadAllLines(dumpedPath)
            .Where(line => !string.IsNullOrEmpty(line))
            .Select(line => ExportHelpers.DecomposeFlatLine(line, options, textFile.EnableSizeShrink))
            .ToList();

        ExportHelpers.WriteExport(workingDirectory, textFile, foundLines);
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
    /// exactly like a RawCsv cell - if any fragment is untranslated, flagged for
    /// retranslation, or unsafe, the whole line falls back to its original raw text rather than
    /// reconstructing a partially-translated result (matching the CSV reconstruction path in
    /// GameFileHandling.PackageFinalTranslationAsync). A trivial (non-templated) line falls back to
    /// its single split's original text under the same conditions.
    /// </summary>
    /// <param name="config">Already-loaded configuration; read from <paramref name="workingDirectory"/> when null.</param>
    public static async Task<(int Passed, int QcRejected, int RawFallback)> PackagePrefabTextAsync(string workingDirectory, TextFileToSplit textFile,
        LlmConfig? config = null)
    {
        var outputPath = $"{workingDirectory}/Mod";
        Directory.CreateDirectory(outputPath);

        config ??= ConfigurationExtensions.GetConfiguration(workingDirectory);
        var qualityReview = config.QualityReview;

        var results = new List<PrefabTextResult>();
        var counts = new PackagingCounts();

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory, [textFile], async (_, _, fileLines) =>
        {
            foreach (var line in fileLines)
            {
                var (result, reason) = ReconstructLine(line, textFile, qualityReview);

                // Count regardless of whether result is null - a RawFallback (or, more rarely, a
                // QcRejected whose Translated also turned out unusable) is a real, reportable
                // failure even though it now deliberately packages nothing rather than raw
                // Chinese text (see ReconstructLine's doc comment).
                counts.Record(reason, result != null);

                if (result == null)
                    continue;

                result = PackagingTextFixups.Apply(config, textFile, null, line.Raw, result);

                results.Add(new PrefabTextResult(line.Raw, result));
            }

            await Task.CompletedTask;
        });

        var serializer = YamlHelper.CreateSerializer();
        await FileHelper.WriteAllTextWithRetryAsync($"{outputPath}/{textFile.Path}.yaml", serializer.Serialize(results));

        return counts.ToTuple();
    }

    /// <summary>
    /// Reconstructs a single line's packaged output. The returned <see cref="PackagingFailureReason"/>
    /// is purely informational, for <see cref="PackagePrefabTextAsync"/>'s reporting counts:
    /// <c>QcRejected</c> means the line packaged fine on its ordinary pre-QC <c>Translated</c> text
    /// (see docs/plans/quality-review-pass.md), but a proposed correction existed and was held back
    /// by <see cref="Utility.QualityReviewHelpers.PassesQcScoreGate"/> (a low score not covered by an
    /// auto-accepted DEFECT category). <c>RawFallback</c> means a fragment/split was unsafe, flagged
    /// for retranslation, or missing its translation entirely, with no usable <c>Translated</c> to
    /// fall back to either.
    ///
    /// Neither failure reason EVER falls back to raw Chinese text (<c>line.Raw</c>/<c>split.Text</c>)
    /// - <c>Result</c> is <c>null</c> for <c>RawFallback</c> (this dictionary is looked up by exact
    /// whole-string match, so omitting the entry simply means no replacement happens and the UI
    /// keeps showing whatever it already had - equivalent in effect to raw fallback, without ever
    /// writing an explicit Chinese "translation" into the dictionary). A held-back QC correction
    /// similarly must never discard an already-good pre-QC translation down to raw Chinese - see
    /// DragonHierOverLlm/Tests/docs/qc-run-startup-crash-investigation-2026-09-15.md for the real
    /// startup-breaking bug an explicit raw-Chinese packaged entry used to cause. This must be
    /// reported by the caller as an actual failure rather than silently folded into the same bucket
    /// as a genuinely successful line, since a `None` outcome and a `RawFallback` outcome can
    /// otherwise look identical (a line simply missing from the packaged YAML). The underlying
    /// <c>Translated</c>/<c>QcTranslated</c>/<c>QcQualityScore</c> values are never modified here
    /// regardless of outcome - this only decides what gets written to <c>Files/Mod</c>, never what's
    /// kept in <c>Files/Converted</c>.
    /// </summary>
    private static (string? Result, PackagingFailureReason Reason) ReconstructLine(TranslationLine line, TextFileToSplit textFile, QualityReviewConfig qualityReview)
    {
        var template = line.Templates.FirstOrDefault(t => t.Split == 0);
        if (template != null)
        {
            var fragments = line.Splits.Where(s => s.Split == 0).OrderBy(s => s.SubIndex).ToList();

            // A low score/unaccepted DEFECT category only skips the QcTranslated shortcut; the
            // already-good pre-QC fragments still reconstruct normally (see
            // DragonHierOverLlm/Tests/docs/qc-run-startup-crash-investigation-2026-09-15.md).
            var resolved = PackagingHelpers.ResolveFragments(fragments, template, textFile, qualityReview, anchorFallsBackToFirst: false);
            return (resolved.Text, resolved.Reason);
        }

        var split = line.Splits.FirstOrDefault(s => s.Split == 0);
        if (split == null)
            return (null, PackagingFailureReason.None);

        var plain = PackagingHelpers.ResolvePlainSplit(split, qualityReview);
        return (plain.Text, plain.Reason);
    }
}
