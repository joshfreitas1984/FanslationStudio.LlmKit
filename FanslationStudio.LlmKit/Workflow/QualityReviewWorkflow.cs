using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Post-translation quality review pass - see docs/plans/quality-review-pass.md (DragonHierOverLlm
/// repo) for the full design. Reviews the fully reconstructed cell for each already-translated
/// column (across every <see cref="TextFileToSplit"/> passed in), independent of the primary
/// translation model/pipeline, and either confirms it or proposes a correction that is validated
/// exactly like a normal translation attempt before being accepted. Never touches a column that
/// hasn't finished normal translation (still flagged for retranslation, unsafe, or missing its
/// translation) - this pass only reviews text the translation pipeline itself already considers
/// "done".
/// </summary>
public static class QualityReviewWorkflow
{
    /// <summary>
    /// Response format the QC prompt asks the model for - deliberately simple (two labeled lines)
    /// rather than JSON, since small/local models are more reliable at producing this than
    /// well-formed JSON. A response that doesn't match <see cref="ScoreLineRegex"/> is treated as
    /// unparseable and the column is left untouched (picked up again next run) rather than
    /// recording a guessed score - see ReviewColumnAsync.
    /// </summary>
    private static readonly Regex ScoreLineRegex = new(@"^\s*SCORE:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex CorrectedLineRegex = new(@"CORRECTED:\s*(.*)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private sealed class QcFileState
    {
        public required TextFileToSplit TextFile { get; init; }
        public required string OutputFile { get; init; }
        public required List<TranslationLine> FileLines { get; init; }
        public required ISerializer Serializer { get; init; }
        public readonly object WriteLock = new();
        public int BufferedRecords;
    }

    /// <summary>One work item per column (a group of <see cref="TranslationSplit"/>s sharing the
    /// same <see cref="TranslationSplit.Split"/> index within one line) - a plain column has exactly
    /// one fragment, a templated/compound column has several, anchored on the fragment with
    /// <see cref="TranslationSplit.SubIndex"/> == 0 (see TranslationSplit.QcTranslated's doc
    /// comment for why only that fragment carries QC state for a templated column).</summary>
    private sealed class QcWorkItem
    {
        public required QcFileState File { get; init; }
        public required TranslationSplit Anchor { get; init; }
        public FieldTemplate? Template { get; init; }
        public required List<TranslationSplit> Fragments { get; init; }
    }

    private enum QcOutcome { Skipped, Passed, Corrected, RejectedByGate }

    /// <summary>
    /// The model's raw verdict for one (source text, current translation, applicable glossary
    /// prompt) triple, before any file/column-specific repair or validation is applied to it -
    /// see <see cref="ReviewCacheKey"/>/<see cref="ReviewLlmCache"/>.
    /// </summary>
    private sealed record LlmVerdict(bool Success, int Score, string? CorrectedRawMasked);

    /// <summary>
    /// SOURCE + CURRENT TRANSLATION + the glossary prompt built for them (see
    /// <see cref="GlossaryLine.AppendPromptsFor"/>) is everything that actually goes into the QC
    /// prompt sent to the model, so it's exactly the right cache key: two columns (even in
    /// different files) that reduce to the same triple get the same LLM verdict instead of two
    /// independent, non-deterministic LLM calls that could disagree with each other on identical
    /// text. Including the glossary prompt (rather than just raw text) rather than the file path
    /// itself keeps this safe even though some glossary lines are restricted to specific output
    /// files (<see cref="GlossaryLine.OnlyOutputFiles"/>/<see cref="GlossaryLine.ExcludeOutputFiles"/>)
    /// - if that restriction makes the applicable glossary content differ between two files, the
    /// prompt (and so the key) differs too, and they naturally get separate LLM calls instead of
    /// wrongly sharing one.
    /// </summary>
    private readonly record struct ReviewCacheKey(string RawText, string EffectiveTranslated, string GlossaryPrompt);

    /// <summary>
    /// Dedupes concurrent/repeated LLM calls for the same <see cref="ReviewCacheKey"/> within one
    /// <see cref="RunAsync"/> run. The <see cref="Lazy{T}"/> wrapper (not just the bare Task) is
    /// what makes this safe under <c>Parallel.ForEachAsync</c>: two threads racing
    /// <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/> for the same key would otherwise
    /// both start their own LLM call before either finishes - wrapping the factory in Lazy ensures
    /// only one of them actually runs it, and the other just awaits the same in-flight Task.
    /// </summary>
    private sealed class ReviewLlmCache : ConcurrentDictionary<ReviewCacheKey, Lazy<Task<LlmVerdict>>>;

    /// <summary>
    /// Runs the quality review pass. See docs/plans/quality-review-pass.md.
    /// </summary>
    /// <param name="sampleSize">
    /// If set, reviews at most this many randomly-selected columns instead of every eligible
    /// column - intended for the plan's "run a small sample before committing to a full pass"
    /// step, so a candidate model's real speed/score-distribution/correction-quality can be judged
    /// on a manageable, representative sample rather than committing an entire run (this repo's
    /// corpus is ~72,500 splits at time of writing) to an unproven model/prompt. The sample is
    /// drawn from every column across every file (not just the first file alphabetically) so it
    /// exercises a representative mix of plain and templated columns. Omit (or pass null) for a
    /// full run once a model has been chosen.
    /// </param>
    public static async Task RunAsync(string workingDirectory, TextFileToSplit[] textFiles, int? sampleSize = null)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory);

        if (!config.QualityReview.Enabled)
        {
            Console.WriteLine("Quality review pass is disabled (qualityReview.enabled: false in Config.yaml) - nothing to do.");
            return;
        }

        if (string.IsNullOrEmpty(config.QualityReview.ModelName)
            || !config.Runtime.Models.TryGetValue(config.QualityReview.ModelName, out var modelConfig))
        {
            throw new InvalidOperationException(
                $"QualityReview.ModelName '{config.QualityReview.ModelName}' does not match any configured model. " +
                $"Configured model names: {string.Join(", ", config.Runtime.Models.Keys)}");
        }

        if (!modelConfig.Prompts.ContainsKey("BaseQualityReviewPrompt"))
        {
            throw new InvalidOperationException(
                "No 'BaseQualityReviewPrompt' prompt configured for the quality review model - " +
                "add a BaseQualityReviewPrompt.txt file under its CustomPromptsPath folder.");
        }

        var maxConcurrency = config.QualityReview.MaxConcurrency ?? config.MaxConcurrency ?? config.BatchSize ?? 20;
        var outputPath = $"{workingDirectory}/Converted";
        var deserializer = YamlHelper.CreateDeserializer();

        var fileStates = new List<QcFileState>();
        foreach (var textFile in textFiles)
        {
            if (!textFile.EnableQualityReview)
                continue;

            var outputFile = $"{outputPath}/{textFile.Path}.yaml";
            if (!File.Exists(outputFile))
                continue;

            var content = await File.ReadAllTextAsync(outputFile);
            var fileLines = deserializer.Deserialize<List<TranslationLine>>(content);

            fileStates.Add(new QcFileState
            {
                TextFile = textFile,
                OutputFile = outputFile,
                FileLines = fileLines,
                Serializer = YamlHelper.CreateSerializer(),
            });
        }

        // Build one work item per column, across every line in every file.
        var workItems = new List<QcWorkItem>();
        foreach (var file in fileStates)
        {
            foreach (var line in file.FileLines)
            {
                foreach (var columnGroup in line.Splits.GroupBy(s => s.Split))
                {
                    var fragments = columnGroup.OrderBy(s => s.SubIndex).ToList();
                    var anchor = fragments.FirstOrDefault(f => f.SubIndex == 0) ?? fragments[0];
                    var template = line.Templates.FirstOrDefault(t => t.Split == columnGroup.Key);

                    workItems.Add(new QcWorkItem
                    {
                        File = file,
                        Anchor = anchor,
                        Template = template,
                        Fragments = fragments,
                    });
                }
            }
        }

        if (sampleSize is int sample && sample < workItems.Count)
        {
            // Random, not first-N - a first-N sample would be biased toward whichever file(s)
            // happen to be enumerated first (e.g. alphabetically), not representative of the mix
            // of plain/templated columns across the whole corpus.
            workItems = workItems.OrderBy(_ => Random.Shared.Next()).Take(sample).ToList();
            Console.WriteLine($"Quality review: sampling {workItems.Count} of the eligible column(s) (sampleSize={sample}).");
        }

        Console.WriteLine($"Quality review: {workItems.Count} column(s) across {fileStates.Count} file(s) to consider, max concurrency {maxConcurrency}, model '{config.QualityReview.ModelName}'.");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var reviewCache = new ReviewLlmCache();

        var consideredCount = 0;
        var reviewedCount = 0;
        var correctedCount = 0;
        var rejectedCount = 0;
        var flaggedCount = 0;

        await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency }, async (item, _) =>
        {
            var outcome = await ReviewColumnAsync(config, modelConfig, client, item, reviewCache);

            if (outcome == QcOutcome.Skipped)
                return;

            var considered = Interlocked.Increment(ref consideredCount);
            Interlocked.Increment(ref reviewedCount);
            if (outcome == QcOutcome.Corrected) Interlocked.Increment(ref correctedCount);
            if (outcome == QcOutcome.RejectedByGate) Interlocked.Increment(ref rejectedCount);
            if (item.Anchor.FlaggedForQcReview) Interlocked.Increment(ref flaggedCount);

            if (considered % TranslationService.BatchlessLog == 0)
                Console.WriteLine($"Quality review progress: {considered} reviewed so far (corrected: {correctedCount}, rejected by gate: {rejectedCount}, flagged: {flaggedCount})");

            var buffered = Interlocked.Increment(ref item.File.BufferedRecords);
            if (buffered > TranslationService.BatchlessBuffer)
            {
                lock (item.File.WriteLock)
                {
                    if (item.File.BufferedRecords > TranslationService.BatchlessBuffer)
                    {
                        FileHelper.WriteAllTextWithRetry(item.File.OutputFile, item.File.Serializer.Serialize(item.File.FileLines));
                        item.File.BufferedRecords = 0;
                    }
                }
            }
        });

        foreach (var file in fileStates)
            await FileHelper.WriteAllTextWithRetryAsync(file.OutputFile, file.Serializer.Serialize(file.FileLines));

        Console.WriteLine($"Quality review done: {reviewedCount} reviewed, {correctedCount} corrected, {rejectedCount} rejected by validation gate, {flaggedCount} flagged for human review.");
    }

    private static async Task<QcOutcome> ReviewColumnAsync(LlmConfig config, ModelExecutionConfig modelConfig, HttpClient client, QcWorkItem item, ReviewLlmCache reviewCache)
    {
        var anchor = item.Anchor;

        // Readiness: every fragment in this column must already have a real, non-flagged
        // translation - reviewing a column mid-retry-loop would waste a call reviewing text
        // that's about to be replaced anyway.
        foreach (var fragment in item.Fragments)
        {
            if (fragment.FlaggedForRetranslation || !fragment.SafeToTranslate)
                return QcOutcome.Skipped;
            if (string.IsNullOrEmpty(fragment.Translated) && !string.IsNullOrEmpty(fragment.Text))
                return QcOutcome.Skipped;
        }

        var rawText = item.Template != null
            ? CompoundFieldSplitter.Reconstruct(item.Template.Template, item.Fragments.Select(f => f.Text).ToList())
            : anchor.Text;

        var effectiveTranslated = QualityReviewHelpers.ComputeEffectiveTranslatedText(anchor, item.Template, item.Fragments);

        if (string.IsNullOrEmpty(effectiveTranslated))
            return QcOutcome.Skipped;

        // Already reviewed and nothing has changed since (Translated is the immutable
        // source-of-truth compared here, never QcTranslated - see QcReviewedText's doc comment) -
        // skip the LLM call entirely. Same freshness check packaging uses to decide whether a
        // prior Qc* outcome can still be trusted (see QualityReviewHelpers.IsQcReviewFresh) -
        // here it means "no re-review needed" instead of "no longer trustworthy".
        if (QualityReviewHelpers.IsQcReviewFresh(anchor, item.Template, item.Fragments))
            return QcOutcome.Skipped;

        // Mask dynamic tokens/placeholders the same way the translation pipeline already does
        // (StringTokenReplacer), so the QC model never sees/mangles a raw `#PlayerName#`-style
        // token or a `{n}` template slot. Restored before validating/storing any correction.
        var tokenReplacer = new StringTokenReplacer();
        var maskedRaw = tokenReplacer.Replace(rawText);
        var maskedTranslated = tokenReplacer.Replace(effectiveTranslated);

        var glossaryPrompt = GlossaryLine.AppendPromptsFor(rawText, config.Runtime.GlossaryLines, item.File.TextFile.Path);

        var cacheKey = new ReviewCacheKey(rawText, effectiveTranslated, glossaryPrompt);
        var verdict = await reviewCache.GetOrAdd(cacheKey, _ => new Lazy<Task<LlmVerdict>>(
            () => GetLlmVerdictAsync(config, modelConfig, client, rawText, maskedRaw, maskedTranslated, glossaryPrompt))).Value;

        if (!verdict.Success)
            // Either the request errored, or the response didn't parse - leave the column's Qc
            // state exactly as it was rather than recording a guessed score. Picked up again next
            // run (GetLlmVerdictAsync already logged the reason).
            return QcOutcome.Skipped;

        // Clean slate before recording this review's outcome - avoids a stale
        // QcTranslated/FlaggedForQcReview lingering from an earlier review of different text.
        anchor.ResetQcState();
        anchor.QcReviewedText = effectiveTranslated;
        anchor.QcQualityScore = verdict.Score;

        if (verdict.CorrectedRawMasked == null)
        {
            anchor.QcStatus = QcStatus.Passed;
            anchor.FlaggedForQcReview = verdict.Score < config.QualityReview.MinAcceptableScore;
            return QcOutcome.Passed;
        }

        // Restore using THIS column's own tokenReplacer - its placeholderMap/sizeMap/colorMap were
        // just populated by this column's own Replace() calls above, which is deterministic given
        // the same rawText/effectiveTranslated (see StringTokenReplacer.Replace), so this correctly
        // reverses the masked correction even when the verdict itself came from the cache/another
        // column's LLM call.
        var correctedResult = tokenReplacer.Restore(verdict.CorrectedRawMasked);

        // Same post-LLM repair pass a normal translation attempt gets (TranslateSplitAsync always
        // runs PrepareResult before validating) - e.g. GameFileHandling.RepairKnownLlmQuirks
        // unwraps braces an LLM sometimes adds around this game's own "#...#" placeholder tokens.
        // A QC correction is just as capable of introducing the same quirks as a normal
        // translation attempt, so skipping this here meant a fixable artifact (e.g. GLM4 rewriting
        // "#TargetInteractName#" as "{TargetInteractName}") went straight to the validation gate
        // and got rejected outright instead of being repaired first like it would on the
        // translation path.
        correctedResult = LineValidation.PrepareResult(rawText, correctedResult, item.File.TextFile, anchor.Split);

        // Validation gate: the exact same structural check a normal translation attempt goes
        // through (LineValidation.CheckTransalationSuccessful), plus the same rule checks
        // ApplyAllRulesToCurrentTranslation applies to every line (bad-words/hallucinated-glossary-
        // term/missing-ellipsis/missing-required-token - a QC correction is just as capable of
        // introducing a bad-words hit or dropping a required token as a fresh translation attempt,
        // and nothing else in this gate would have caught that), plus checks specific to QC that
        // need a "known-good" baseline to compare against - something a fresh translation attempt
        // from raw Chinese doesn't have, but a QC correction does (see
        // CheckGlossaryDrift/CheckCapitalizationRegression).
        var validation = LineValidation.CheckTransalationSuccessful(modelConfig, rawText, correctedResult, item.File.TextFile, anchor.Split);
        var glossaryFailureReason = CheckGlossaryDrift(rawText, correctedResult, config.Runtime.GlossaryLines, item.File.TextFile.Path);
        var capsFailureReason = CheckCapitalizationRegression(effectiveTranslated, correctedResult);
        var badWordsFailureReason = TranslationWorkflow.MatchesBadWords(correctedResult)
            ? $"QC correction matches the bad-words list: '{correctedResult}'."
            : null;
        var ellipsisFailureReason = TranslationWorkflow.IsMissingRequiredEllipsis(rawText, correctedResult)
            ? "QC correction is missing an ellipsis '...' required by the source."
            : null;
        var missingTokenFailureReason = TranslationWorkflow.FindMissingRequiredToken(rawText, correctedResult, config.ExtraStringTokenReplacers) is string missingToken
            ? $"QC correction is missing required token '{missingToken}'."
            : null;
        var hallucinationFailureReason = TranslationWorkflow.FindGlossaryHallucination(rawText, correctedResult, config, item.File.TextFile);

        var failureReason = glossaryFailureReason ?? capsFailureReason ?? badWordsFailureReason
            ?? ellipsisFailureReason ?? missingTokenFailureReason ?? hallucinationFailureReason
            ?? (validation.Valid ? null : validation.CorrectionPrompt);

        if (failureReason != null)
        {
            anchor.QcStatus = QcStatus.FailedValidation;
            anchor.QcRejectedCorrection = correctedResult;
            anchor.QcFailureReason = failureReason;
            anchor.FlaggedForQcReview = true;
            return QcOutcome.RejectedByGate;
        }

        anchor.QcStatus = QcStatus.Corrected;
        anchor.QcTranslated = correctedResult;
        anchor.FlaggedForQcReview = verdict.Score < config.QualityReview.MinAcceptableScore;
        return QcOutcome.Corrected;
    }

    /// <summary>
    /// Makes the actual QC LLM call for one <see cref="ReviewCacheKey"/> and parses its response.
    /// Pure with respect to any one <see cref="QcWorkItem"/> - deliberately has no knowledge of
    /// which column(s) requested it, so its result can be safely shared by every column that
    /// reduces to the same (source, current translation, glossary prompt) triple (see
    /// <see cref="ReviewLlmCache"/>). File/column-specific repair and validation happen afterward,
    /// back in <see cref="ReviewColumnAsync"/>, once per column.
    /// </summary>
    private static async Task<LlmVerdict> GetLlmVerdictAsync(
        LlmConfig config,
        ModelExecutionConfig modelConfig,
        HttpClient client,
        string rawText,
        string maskedRaw,
        string maskedTranslated,
        string glossaryPrompt)
    {
        var userPrompt = new StringBuilder();
        userPrompt.AppendLine($"SOURCE (Chinese): {maskedRaw}");
        userPrompt.AppendLine($"CURRENT TRANSLATION (English): {maskedTranslated}");
        if (!string.IsNullOrEmpty(glossaryPrompt))
        {
            userPrompt.AppendLine("Relevant glossary terms (must be preserved if they appear in SOURCE):");
            userPrompt.AppendLine(glossaryPrompt);
        }

        var messages = new List<object>
        {
            LlmHelpers.GenerateSystemPrompt(modelConfig.Prompts["BaseQualityReviewPrompt"]),
            LlmHelpers.GenerateUserPrompt(userPrompt.ToString()),
        };

        string llmResponse;
        try
        {
            llmResponse = await TranslationService.TranslateMessagesAsync(client, config, modelConfig, messages);
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"Quality review request error: {e.Message}");
            return new LlmVerdict(false, 0, null);
        }

        var scoreMatch = ScoreLineRegex.Match(llmResponse);
        if (!scoreMatch.Success)
        {
            Console.WriteLine($"Quality review: could not parse response for '{rawText}' - skipping. Raw response: {llmResponse}");
            return new LlmVerdict(false, 0, null);
        }

        var score = Math.Clamp(int.Parse(scoreMatch.Groups[1].Value), 0, 100);

        var correctedMatch = CorrectedLineRegex.Match(llmResponse);
        var correctedRaw = correctedMatch.Success ? correctedMatch.Groups[1].Value.Trim() : string.Empty;
        var hasCorrection = correctedMatch.Success
            && !string.IsNullOrEmpty(correctedRaw)
            && !correctedRaw.Equals("NONE", StringComparison.OrdinalIgnoreCase);

        return new LlmVerdict(true, score, hasCorrection ? correctedRaw : null);
    }

    /// <summary>
    /// Every glossary term whose Raw/RawSimplified/RawTraditional matched in <paramref name="rawText"/>
    /// must still have its Result (or an allowed alternative) present in <paramref name="correctedText"/> -
    /// a QC correction that silently drops or changes a glossary-mapped name/term is rejected, even
    /// if it otherwise passes the generic validation checks. See docs/plans/quality-review-pass.md's
    /// "make sure it doesn't... mistranslate things in the glossary after running" requirement.
    /// </summary>
    private static string? CheckGlossaryDrift(string rawText, string correctedText, List<GlossaryLine> glossaryLines, string outputFile)
    {
        foreach (var line in glossaryLines)
        {
            if (line.OnlyOutputFiles.Count > 0 && !line.OnlyOutputFiles.Contains(outputFile))
                continue;
            if (line.ExcludeOutputFiles.Count > 0 && line.ExcludeOutputFiles.Contains(outputFile))
                continue;

            var matchedRaw = (!string.IsNullOrEmpty(line.Raw) && rawText.Contains(line.Raw))
                || (!string.IsNullOrEmpty(line.RawSimplified) && rawText.Contains(line.RawSimplified))
                || (!string.IsNullOrEmpty(line.RawTraditional) && rawText.Contains(line.RawTraditional));

            if (!matchedRaw)
                continue;

            var acceptableResults = new List<string> { line.Result };
            acceptableResults.AddRange(line.AllowedAlternatives);

            if (!acceptableResults.Any(r => !string.IsNullOrEmpty(r) && correctedText.Contains(r, StringComparison.OrdinalIgnoreCase)))
                return $"Glossary term '{line.Raw}' (expected '{line.Result}') is missing from the QC-corrected text.";
        }

        return null;
    }

    /// <summary>
    /// Rejects a QC correction that turns a normally-cased translation into an all-caps "shout" -
    /// e.g. "Full helmet" -> "FULL HELMET". A QC model can rate its own such correction 100/100
    /// (<see cref="TranslationSplit.QcQualityScore"/> is self-reported, not calibrated - see its doc
    /// comment), and <see cref="LineValidation.CheckTransalationSuccessful"/> has no notion of
    /// "natural" casing since a fresh translation attempt has no prior English text to compare
    /// against. A QC correction is different: <paramref name="originalTranslated"/> (the
    /// already-accepted <see cref="TranslationSplit.Translated"/>/prior <see cref="TranslationSplit.QcTranslated"/>
    /// this review started from) is a known-good casing baseline, so a flip from mixed/lower case to
    /// all-caps is a strong, cheap signal of a bad correction rather than a deliberate stylistic
    /// choice. Ignores text with no lowercase letters to begin with (e.g. already an acronym, or too
    /// short to have case at all) so it only fires on an actual regression.
    /// </summary>
    internal static string? CheckCapitalizationRegression(string originalTranslated, string correctedText)
    {
        bool IsAllCapsShout(string text) => text.Any(char.IsUpper) && !text.Any(char.IsLower);

        if (IsAllCapsShout(correctedText) && !IsAllCapsShout(originalTranslated))
            return $"QC correction changed normal casing to all-caps ('{originalTranslated}' -> '{correctedText}').";

        return null;
    }

    /// <summary>
    /// Mirrors <see cref="GameFileHandlingBase.GetFailedTranslations"/>'s reporting shape, scoped to
    /// QC rejections/flags instead of translation failures - lets a human reviewer pull every column
    /// currently flagged for a look (either a rejected correction, or a low quality score) in one
    /// pass, without reading every line in Files/Converted. <see cref="QcReviewedText"/> (not the
    /// column's current <c>Translated</c>) is reported as "the translation" here deliberately -
    /// it's the exact text QC actually judged to produce this flag, which is also what a stale
    /// review would fail to match against a since-changed <c>Translated</c> (see
    /// <see cref="Utility.QualityReviewHelpers.IsQcReviewFresh"/>).
    /// </summary>
    public record FlaggedQcReview(string FilePath, string Text, string QcReviewedText, string? RejectedCorrection, string? Reason, int? Score);

    public static async Task<List<FlaggedQcReview>> GetFlaggedQcReviews(string workingDirectory, TextFileToSplit[] textFiles)
    {
        var flagged = new List<FlaggedQcReview>();

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory, textFiles, async (_, textFile, fileLines) =>
        {
            if (!textFile.EnableQualityReview)
                return;

            foreach (var line in fileLines)
            {
                // Group by column (same shape as RunAsync's work items) rather than iterating
                // raw Splits directly - a templated column's Qc state lives only on its
                // SubIndex == 0 fragment, but that fragment's OWN Text/Translated is just its own
                // piece of the cell, not the whole reconstructed raw text QC actually reviewed.
                foreach (var columnGroup in line.Splits.GroupBy(s => s.Split))
                {
                    var fragments = columnGroup.OrderBy(s => s.SubIndex).ToList();
                    var anchor = fragments.FirstOrDefault(f => f.SubIndex == 0) ?? fragments[0];

                    if (!anchor.FlaggedForQcReview)
                        continue;

                    var template = line.Templates.FirstOrDefault(t => t.Split == columnGroup.Key);
                    var rawText = template != null
                        ? CompoundFieldSplitter.Reconstruct(template.Template, fragments.Select(f => f.Text).ToList())
                        : anchor.Text;

                    flagged.Add(new FlaggedQcReview(
                        textFile.Path,
                        rawText,
                        anchor.QcReviewedText,
                        string.IsNullOrEmpty(anchor.QcRejectedCorrection) ? null : anchor.QcRejectedCorrection,
                        string.IsNullOrEmpty(anchor.QcFailureReason) ? null : anchor.QcFailureReason,
                        anchor.QcQualityScore));
                }
            }

            await Task.CompletedTask;
        });

        return flagged;
    }
}
