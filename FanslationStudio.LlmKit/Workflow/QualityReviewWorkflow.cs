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

    /// <summary>
    /// Anchored to a single line (Multiline, no Singleline) so a response that restates or trails
    /// text after its "CORRECTED: ..." line (e.g. the model repeating the label, or adding stray
    /// commentary despite the prompt's "nothing else" instruction) doesn't get swept into the
    /// captured group - see the correction-suffix-leak postmortem in DragonHierOverLlm's
    /// docs/plans/quality-review-pass.md for a real example of the corrupted output this produced
    /// before this fix (a stored QcTranslated like "Wealth in the millions CORRECTED: NONE").
    /// </summary>
    private static readonly Regex CorrectedLineRegex = new(@"^\s*CORRECTED:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

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
    /// <returns>
    /// How many columns were actually reviewed this pass (got a real LLM verdict - Passed,
    /// Corrected, or RejectedByGate - as opposed to being Skipped). Used by
    /// <see cref="RunBruteForce"/> as a second loop-continuation signal alongside
    /// <see cref="ApplyRulesToCurrentQcTranslated"/>'s count: a column left <see cref="QcStatus.NotReviewed"/>
    /// for another retry after a rejection (see the RejectedByGate branch in
    /// <see cref="ReviewColumnAsync"/>) is invisible to that count, since it only tracks
    /// <see cref="QcStatus.Corrected"/> columns going stale - so without this, the loop could stop
    /// while a retriable rejection is still sitting there waiting for its next attempt.
    /// </returns>
    public static async Task<int> RunAsync(string workingDirectory, TextFileToSplit[] textFiles, int? sampleSize = null, GameHooks? hooks = null)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory, hooks);

        if (!config.QualityReview.Enabled)
        {
            Console.WriteLine("Quality review pass is disabled (qualityReview.enabled: false in Config.yaml) - nothing to do.");
            return 0;
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

        return reviewedCount;
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
            // Decided here, at the point the score is actually known, rather than deferred to a
            // separate step (ApplyRulesToCurrentQcTranslated) that only runs as part of
            // RunBruteForce - a plain RunAsync call must leave QcStatus just as accurate as a
            // brute-forced one, not dependent on which caller happened to invoke this.
            if (TryRetryForLowScore(config, anchor, effectiveTranslated, verdict.Score, item.File.TextFile))
                return QcOutcome.Passed;

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
        correctedResult = LineValidation.PrepareResult(rawText, correctedResult, config.Hooks, item.File.TextFile, anchor.Split);

        // Validation gate: TranslationWorkflow.EvaluateRules is the single shared rule list a
        // candidate translation must pass - the same one ApplyTranslationRules runs against
        // Translated and ApplyRulesToCurrentQcTranslated re-runs against an already-accepted
        // QcTranslated, so a new rule added there applies here too with nothing else to edit.
        //
        // preparedRaw gets the CJK-punctuation-normalized half of LineValidation.PrepareRaw (e.g.
        // the CJK ellipsis glyph "…" -> "..." so IsMissingRequiredEllipsis can actually match it) -
        // called with a null tokenReplacer so it does NOT also run the token-masking half, which
        // would corrupt this column's own tokenReplacer (already populated above for the masked LLM
        // prompt/Restore); splitRaw stays the untouched rawText, matching what the Translated path
        // passes for the same parameter.
        // Layered on top: CheckCapitalizationRegression, a check specific to QC that needs a
        // "known-good" baseline to compare against - something a fresh translation attempt from raw
        // Chinese doesn't have, but a QC correction does.
        var preparedRaw = LineValidation.PrepareRaw(rawText, null);
        var ruleResult = TranslationWorkflow.EvaluateRules(config, modelConfig, preparedRaw, rawText, correctedResult, item.File.TextFile, anchor.Split);
        var capsFailureReason = CheckCapitalizationRegression(effectiveTranslated, correctedResult);

        var failureReason = capsFailureReason ?? ruleResult.AllReasons.FirstOrDefault();

        if (failureReason != null)
        {
            // Same underlying Translated as last time this column was rejected? Keep counting
            // toward the retry cap (TranslationSplit.QcRuleCheckFailureCount) - shared with
            // ApplyRulesToCurrentQcTranslated's own retry tracking for a Corrected column going
            // stale, since both are "this column's QC output keeps breaking a rule" from the
            // column's point of view. Anything else (first rejection ever, or Translated changed
            // since) starts the count over.
            anchor.QcRuleCheckFailureCount = anchor.QcRuleCheckFailureBaseline == effectiveTranslated
                ? anchor.QcRuleCheckFailureCount + 1
                : 1;
            anchor.QcRuleCheckFailureBaseline = effectiveTranslated;
            anchor.QcRejectedCorrection = correctedResult;
            anchor.QcFailureReason = failureReason;

            if (anchor.QcRuleCheckFailureCount > config.QualityReview.MaxRuleCheckRetries)
            {
                // Given up: leave QcReviewedText matching (set above) so IsQcReviewFresh reports
                // this as "already handled" - RunAsync stops re-reviewing it, and it surfaces for a
                // human via GetFlaggedQcReviews, same terminal shape
                // ApplyRulesToCurrentQcTranslated's own give-up path uses. QcQualityScore must be
                // cleared too (not just QcTranslated, already empty from ResetQcState() above) -
                // packaging's low-score gate (TranslationPackaging.cs) checks
                // "qcFresh && QcQualityScore < minAcceptableScore" BEFORE it ever reaches the
                // QcTranslated-empty fallback to Translated, so a stale/low verdict.Score left behind
                // here would make the whole line fail outright (raw source shipped) instead of the
                // intended clean fallback to the last known-good Translated.
                anchor.QcStatus = QcStatus.FailedValidation;
                anchor.QcQualityScore = null;
                anchor.FlaggedForQcReview = true;
                return QcOutcome.RejectedByGate;
            }

            // Still worth another try - deliberately do NOT leave QcReviewedText matching
            // effectiveTranslated (undoing what the ResetQcState()+assignment above just set), so
            // IsQcReviewFresh reports this column as needing review again and the next RunAsync
            // pass retries it automatically - no extra plumbing needed in RunBruteForce's loop.
            // FlaggedForQcReview stays false while still retrying automatically; only surfaced once
            // given up.
            anchor.QcStatus = QcStatus.NotReviewed;
            anchor.QcReviewedText = string.Empty;
            return QcOutcome.RejectedByGate;
        }

        // Same reasoning as the Passed branch above - decided here, at review time, not deferred.
        // A retry here discards correctedResult entirely (QcTranslated stays empty, from
        // ResetQcState() above) rather than half-accepting it, exactly like a rule-violation retry
        // never keeps the rejected text around either - it's not this attempt's job to accept
        // anything it isn't confident enough to keep.
        if (TryRetryForLowScore(config, anchor, effectiveTranslated, verdict.Score, item.File.TextFile))
            return QcOutcome.Corrected;

        anchor.QcStatus = QcStatus.Corrected;
        anchor.QcTranslated = correctedResult;
        anchor.FlaggedForQcReview = verdict.Score < config.QualityReview.MinAcceptableScore;
        return QcOutcome.Corrected;
    }

    /// <summary>
    /// Shared by <see cref="ReviewColumnAsync"/>'s Passed and accepted-Corrected paths: decides
    /// whether <paramref name="score"/> is worth another try, using the SAME retry budget
    /// (<see cref="TranslationSplit.QcRuleCheckFailureCount"/>/<see cref="Configuration.QualityReviewConfig.MaxRuleCheckRetries"/>)
    /// a rule violation gets - computed right here, at the point the score is actually known,
    /// rather than deferred to <see cref="ApplyRulesToCurrentQcTranslated"/>, a separate step that
    /// only runs as part of <see cref="RunBruteForce"/>. A plain <see cref="RunAsync"/> call must
    /// leave <see cref="TranslationSplit.QcStatus"/> just as accurate as a brute-forced one.
    ///
    /// Returns true if the caller should retry: <paramref name="anchor"/> is already left
    /// <see cref="QcStatus.NotReviewed"/> (not fresh) for the next <see cref="RunAsync"/> pass to
    /// pick up, and the caller must not accept whatever it was about to record (mirrors how a
    /// rule-violation retry never half-accepts the rejected text either). Returns false if the
    /// score already clears the bar, or if retries are exhausted and the caller should just accept
    /// the column as final - a low score isn't a defect to discard, it's a confidence signal, so
    /// giving up here means keeping the result exactly as reviewed, just still flagged for a human.
    /// </summary>
    private static bool TryRetryForLowScore(LlmConfig config, TranslationSplit anchor, string effectiveTranslated, int score, TextFileToSplit textFile)
    {
        if (score >= config.QualityReview.MinAcceptableScore)
        {
            anchor.QcRuleCheckFailureCount = 0;
            anchor.QcRuleCheckFailureBaseline = string.Empty;
            return false;
        }

        anchor.QcRuleCheckFailureCount = anchor.QcRuleCheckFailureBaseline == effectiveTranslated
            ? anchor.QcRuleCheckFailureCount + 1
            : 1;
        anchor.QcRuleCheckFailureBaseline = effectiveTranslated;

        if (anchor.QcRuleCheckFailureCount > config.QualityReview.MaxRuleCheckRetries)
        {
            Console.WriteLine($"Quality review: {textFile.Path} gave up chasing a higher score after {anchor.QcRuleCheckFailureCount} attempts (score {score}) - keeping this result.");
            return false;
        }

        Console.WriteLine($"Quality review: {textFile.Path} score {score} below minimum ({config.QualityReview.MinAcceptableScore}), retry {anchor.QcRuleCheckFailureCount}/{config.QualityReview.MaxRuleCheckRetries}.");
        anchor.QcStatus = QcStatus.NotReviewed;
        anchor.QcReviewedText = string.Empty;
        return true;
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
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
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

        // Defense in depth for the correction-suffix-leak this project already hit once (see
        // CorrectedLineRegex's doc comment) - even with that regex anchored to a single line, a
        // model that restates the "CORRECTED:" label INSIDE its own corrected text (rather than on
        // a trailing line the anchor already excludes) would still leak it into correctedRaw. Any
        // stored QcTranslated containing this label is definitely not a clean translation, so treat
        // it exactly like an unparseable response - reviewed again next run - rather than risk
        // storing/packaging it.
        if (correctedRaw.Contains("CORRECTED:", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Quality review: parsed correction for '{rawText}' still contains a 'CORRECTED:' label - treating as unparseable. Raw response: {llmResponse}");
            return new LlmVerdict(false, 0, null);
        }

        var hasCorrection = correctedMatch.Success
            && !string.IsNullOrEmpty(correctedRaw)
            && !correctedRaw.Equals("NONE", StringComparison.OrdinalIgnoreCase);

        return new LlmVerdict(true, score, hasCorrection ? correctedRaw : null);
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
            // correctedText itself isn't repeated here - it's already stored in QcRejectedCorrection
            // right alongside this reason. originalTranslated is worth keeping though: it's the only
            // place the "before" state shows up, and it's what makes this reason self-explanatory.
            return $"QC correction changed normal casing to all-caps (was '{originalTranslated}').";

        return null;
    }

    /// <summary>
    /// The quality-review equivalent of <see cref="TranslationWorkflow.TranslateLinesBruteForce"/> -
    /// meant to be run as the last step of the normal "added a glossary entry / got file updates /
    /// exported more dynamic strings / added a bad word / needed a new game repair" workflow,
    /// immediately after <c>TranslateLinesBruteForce</c>, so <see cref="TranslationSplit.QcTranslated"/>
    /// gets the same keep-redoing-until-clean treatment <see cref="TranslationSplit.Translated"/>
    /// already gets there instead of silently drifting out of sync with today's rules until someone
    /// happens to re-run a plain <see cref="RunAsync"/>. Each iteration resets any
    /// currently-accepted <c>QcTranslated</c> that now breaks the rules
    /// (<see cref="ApplyRulesToCurrentQcTranslated"/>) and then reviews everything eligible
    /// (<see cref="RunAsync"/> - covers freshly reset columns, any column that has never been
    /// reviewed, e.g. new translations <c>TranslateLinesBruteForce</c> just produced, AND any column
    /// a prior iteration's rejection left <see cref="QcStatus.NotReviewed"/> for another try - see
    /// the <c>RejectedByGate</c> branch in <see cref="ReviewColumnAsync"/>). Stops once BOTH signals
    /// go quiet - the rule-check finds nothing left to reset, AND <see cref="RunAsync"/> reviewed
    /// nothing this pass - or <paramref name="maxIterations"/> is reached. Both are needed: a
    /// column left <c>NotReviewed</c> for a retry is invisible to the rule-check (which only tracks
    /// <see cref="QcStatus.Corrected"/> columns going stale), so relying on that count alone could
    /// stop the loop while a retriable rejection is still waiting for its next attempt.
    /// <paramref name="maxIterations"/> defaults far lower than
    /// <see cref="TranslationWorkflow.TranslateLinesBruteForce"/>'s 30: a genuinely fixable QC rule
    /// break (e.g. a stale correction after a glossary change) typically resolves on the very next
    /// review, unlike a from-scratch translation attempt where more resampling keeps helping, so
    /// there's little to gain from a large shared cap here - <see cref="TranslationSplit.QcRuleCheckFailureCount"/>
    /// bounds any one stuck column's own retries independently anyway.
    /// </summary>
    public static async Task RunBruteForce(string workingDirectory, TextFileToSplit[] textFiles, int? sampleSize = null, int maxIterations = 5, GameHooks? hooks = null)
    {
        var iterations = 0;
        int flagged;
        int reviewed;

        // Catches any already-corrected column left stale by a previous run (e.g. a glossary/bad-
        // word change made since) before spending an LLM call reviewing it - RunAsync's own
        // freshness check would eventually catch this too, but only after this reset makes it
        // non-fresh.
        await ApplyRulesToCurrentQcTranslated(workingDirectory, textFiles, hooks);

        do
        {
            reviewed = await RunAsync(workingDirectory, textFiles, sampleSize, hooks);
            flagged = await ApplyRulesToCurrentQcTranslated(workingDirectory, textFiles, hooks);
            iterations++;
        }
        while ((flagged > 0 || reviewed > 0) && iterations < maxIterations);
    }

    /// <summary>
    /// Retroactively re-applies the same rule set <see cref="ReviewColumnAsync"/>'s validation gate
    /// checks against every column's CURRENTLY ACCEPTED <see cref="TranslationSplit.QcTranslated"/> -
    /// the <see cref="TranslationWorkflow.ApplyAllRulesToCurrentTranslation"/> equivalent for QC
    /// output. Needed because that method only ever looks at <see cref="TranslationSplit.Translated"/>,
    /// so a rule that changes after a correction was already accepted (a new bad word, a
    /// new/changed glossary entry, a new game-specific repair) would otherwise never get re-checked
    /// against an already "fresh" <c>QcTranslated</c> - freshness
    /// (<see cref="QualityReviewHelpers.IsQcReviewFresh"/>) only tracks whether the underlying
    /// <c>Translated</c> has changed, not whether <c>QcTranslated</c> itself is still rule-compliant.
    ///
    /// Two tiers, mirroring <see cref="TranslationWorkflow.UpdateSplit"/>/<see cref="TranslationWorkflow.ApplyTranslationRules"/>:
    /// a deterministic repair (<see cref="LineValidation.PrepareResult"/>/<see cref="LineValidation.CleanupLineBeforeSaving"/>,
    /// same as a normal translation attempt and <see cref="ReviewColumnAsync"/>'s own correction
    /// path get) is applied to <c>QcTranslated</c> in place; anything that can't be fixed
    /// deterministically (bad words, glossary drift/hallucination, missing ellipsis/required token,
    /// generic structural validation - the exact same checks <see cref="ReviewColumnAsync"/>'s gate
    /// runs against a freshly proposed correction) resets the column back to
    /// <see cref="QcStatus.NotReviewed"/> via <see cref="TranslationSplit.ResetQcState"/> for another
    /// try, or - once <see cref="TranslationSplit.QcRuleCheckFailureCount"/> exceeds
    /// <see cref="Configuration.QualityReviewConfig.MaxRuleCheckRetries"/> for this same underlying
    /// <c>Translated</c> - gives up and lands on <see cref="QcStatus.FailedValidation"/> instead, so
    /// a persistently unfixable correction (e.g. a false-positive bad-words match) stops consuming
    /// review passes and surfaces for a human instead. Either way, because packaging always calls
    /// <see cref="QualityReviewHelpers.IsQcReviewFresh"/> first, the column falls back to
    /// (already rule-checked) <c>Translated</c> immediately - never ships broken text while waiting
    /// for (or having given up on) the next LLM review.
    ///
    /// A structurally clean column (no rule violation) - whether <see cref="QcStatus.Corrected"/> or
    /// <see cref="QcStatus.Passed"/> - with a <see cref="TranslationSplit.QcQualityScore"/> still
    /// below <see cref="Configuration.QualityReviewConfig.MinAcceptableScore"/> is just woken up
    /// here (<see cref="QcStatus.NotReviewed"/>, not fresh) rather than having its retry-vs-give-up
    /// decision made in this pass: that decision lives entirely in <see cref="ReviewColumnAsync"/>'s
    /// <c>TryRetryForLowScore</c>, computed at the point the score is actually known, so a column's
    /// status is just as accurate whether it was last touched by a plain <see cref="RunAsync"/> call
    /// or by this retroactive rule-check - never dependent on which one happened to run.
    /// </summary>
    /// <returns>
    /// How many columns still need another <see cref="RunAsync"/> attempt after this pass - newly
    /// reset for retry, or already sitting <see cref="QcStatus.NotReviewed"/> mid-retry from an
    /// earlier pass whose <see cref="RunAsync"/> call skipped it (e.g. an LLM request error) rather
    /// than producing a real verdict. A column that gave up is tracked separately and not counted
    /// here - see <see cref="RunBruteForce"/>.
    /// </returns>
    public static async Task<int> ApplyRulesToCurrentQcTranslated(string workingDirectory, TextFileToSplit[] textFiles, GameHooks? hooks = null)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory, hooks);
        var serializer = YamlHelper.CreateSerializer();
        var totalFlagged = 0;
        var totalGivenUp = 0;

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory, textFiles, async (outputFile, textFile, fileLines) =>
        {
            if (!textFile.EnableQualityReview)
                return;

            var fileModified = false;
            var fileFlagged = 0;
            var fileGivenUp = 0;

            foreach (var line in fileLines)
            {
                foreach (var columnGroup in line.Splits.GroupBy(s => s.Split))
                {
                    var fragments = columnGroup.OrderBy(s => s.SubIndex).ToList();
                    var anchor = fragments.FirstOrDefault(f => f.SubIndex == 0) ?? fragments[0];

                    // A column left NotReviewed mid-retry (QcRuleCheckFailureCount > 0) has nothing
                    // for ApplyRulesToQcColumn to repair/validate - it's waiting on RunAsync, not on
                    // this rule-check. But it still represents unfinished work, and RunAsync's own
                    // reviewedCount won't reflect that if the LLM call for it errored or returned an
                    // unparseable response this pass (ReviewColumnAsync deliberately leaves such a
                    // column's state untouched rather than guessing - see its QcOutcome.Skipped
                    // branch). Without counting it here too, RunBruteForce's loop could see
                    // "0 reviewed, 0 to reset" and stop even though this column never actually got
                    // its next real attempt.
                    if (anchor.QcStatus == QcStatus.NotReviewed)
                    {
                        if (anchor.QcRuleCheckFailureCount > 0)
                            fileFlagged++;
                        continue;
                    }

                    // Corrected columns get the full rule re-check below; Passed columns skip
                    // straight to the low-score retry check inside ApplyRulesToQcColumn (there's no
                    // QcTranslated of their own to repair/validate). FailedValidation is out of scope
                    // here - it's a terminal state (see ResetQcRetryLimits to un-stick one manually).
                    if (anchor.QcStatus != QcStatus.Corrected && anchor.QcStatus != QcStatus.Passed)
                        continue;
                    if (anchor.QcStatus == QcStatus.Corrected && string.IsNullOrEmpty(anchor.QcTranslated))
                        continue;

                    var template = line.Templates.FirstOrDefault(t => t.Split == columnGroup.Key);
                    var rawText = template != null
                        ? CompoundFieldSplitter.Reconstruct(template.Template, fragments.Select(f => f.Text).ToList())
                        : anchor.Text;
                    var priorTranslated = QualityReviewHelpers.ComputeEffectiveTranslatedText(anchor, template, fragments);

                    var (changed, needsRetry, gaveUp) = ApplyRulesToQcColumn(config, anchor, textFile, rawText, priorTranslated);
                    if (changed)
                        fileModified = true;
                    if (needsRetry)
                        fileFlagged++;
                    if (gaveUp)
                        fileGivenUp++;
                }
            }

            if (fileModified)
                await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));

            Interlocked.Add(ref totalFlagged, fileFlagged);
            Interlocked.Add(ref totalGivenUp, fileGivenUp);
        });

        if (totalFlagged > 0 || totalGivenUp > 0)
            Console.WriteLine($"Quality review rule check: {totalFlagged} column(s) reset for re-review (rule violation or low score), {totalGivenUp} gave up after exhausting retries and were left for human review.");

        return totalFlagged;
    }

    /// <summary>
    /// One column's worth of <see cref="ApplyRulesToCurrentQcTranslated"/> - see its doc comment for
    /// the tiering rationale. <paramref name="priorTranslated"/> is the pre-QC baseline
    /// (<see cref="TranslationSplit.Translated"/>, reconstructed for a templated column) used both
    /// by <see cref="CheckCapitalizationRegression"/> (exactly as <see cref="ReviewColumnAsync"/>
    /// uses it) and as the retry-counter baseline for a <see cref="QcStatus.Passed"/> column, which
    /// has no <see cref="TranslationSplit.QcTranslated"/> of its own to key off.
    /// </summary>
    private static (bool changed, bool needsRetry, bool gaveUp) ApplyRulesToQcColumn(
        LlmConfig config,
        TranslationSplit anchor,
        TextFileToSplit textFile,
        string rawText,
        string priorTranslated)
    {
        // A Passed column has no QcTranslated of its own to repair/rule-check - the only thing left
        // to potentially wake up is a low score.
        if (anchor.QcStatus == QcStatus.Passed)
            return WakeUpIfLowScore(config, anchor, textFile, alreadyChanged: false);

        var current = anchor.QcTranslated;

        // Tier 1: deterministic repair, in place - same repair a normal translation attempt (and a
        // freshly proposed QC correction) already gets before ever reaching validation.
        var repaired = LineValidation.PrepareResult(rawText, current, config.Hooks, textFile, anchor.Split);
        var cleaned = LineValidation.CleanupLineBeforeSaving(repaired, rawText, textFile, new StringTokenReplacer());

        var changed = cleaned != current;
        if (changed)
            anchor.QcTranslated = current = cleaned;

        // Tier 2: the exact same shared rule list ReviewColumnAsync's validation gate runs against a
        // freshly proposed correction (see TranslationWorkflow.EvaluateRules) - anything that fails
        // here can't be fixed deterministically, so the column is reset for a fresh LLM review
        // instead. preparedRaw is rawText's CJK-punctuation-normalized form (null tokenReplacer - no
        // masking needed here, nothing downstream needs to Restore it) - see ReviewColumnAsync's
        // matching comment for why this matters (e.g. the ellipsis check).
        var modelConfig = LlmHelpers.CalculateModelConfig(config, rawText);
        var preparedRaw = LineValidation.PrepareRaw(rawText, null);
        var ruleResult = TranslationWorkflow.EvaluateRules(config, modelConfig, preparedRaw, rawText, current, textFile, anchor.Split);
        var capsFailureReason = CheckCapitalizationRegression(priorTranslated, current);

        var failureReason = capsFailureReason ?? ruleResult.AllReasons.FirstOrDefault();

        if (failureReason == null)
            // Structurally clean - if the score is still low, wake it up for a fresh review rather
            // than deciding retry-vs-give-up here (see WakeUpIfLowScore's doc comment for why that
            // decision now lives entirely in ReviewColumnAsync).
            return WakeUpIfLowScore(config, anchor, textFile, alreadyChanged: changed);

        // Same underlying Translated as last time this column failed? Keep counting toward the
        // retry cap. Anything else (first failure ever, or Translated changed since - a
        // retranslation, manual fix, or repair) starts the count over: that's a different piece of
        // text with no accumulated history of its own, so it deserves a full retry budget.
        anchor.QcRuleCheckFailureCount = anchor.QcRuleCheckFailureBaseline == priorTranslated
            ? anchor.QcRuleCheckFailureCount + 1
            : 1;
        anchor.QcRuleCheckFailureBaseline = priorTranslated;

        if (anchor.QcRuleCheckFailureCount > config.QualityReview.MaxRuleCheckRetries)
        {
            // Given up: discard the correction (same safe fallback to Translated as every other
            // reset), but land on FailedValidation instead of NotReviewed so RunAsync stops
            // re-reviewing it and it surfaces for a human via GetFlaggedQcReviews, exactly like a
            // freshly-rejected correction already does. QcReviewedText = priorTranslated marks it
            // "fresh" so packaging/RunAsync don't treat it as needing another look. QcQualityScore
            // must be cleared too - packaging's low-score gate (TranslationPackaging.cs) checks
            // "qcFresh && QcQualityScore < minAcceptableScore" BEFORE it ever reaches the
            // QcTranslated-empty fallback to Translated, so leaving behind whatever score this
            // column had from its original (now-discarded) Corrected acceptance would make the
            // whole line fail outright (raw source shipped) instead of the intended clean fallback.
            Console.WriteLine($"Quality review rule check: {textFile.Path} gave up after {anchor.QcRuleCheckFailureCount} retries ({failureReason}) \n{current}");
            anchor.QcTranslated = string.Empty;
            anchor.QcStatus = QcStatus.FailedValidation;
            anchor.QcReviewedText = priorTranslated;
            anchor.QcRejectedCorrection = current;
            anchor.QcFailureReason = $"Gave up after {anchor.QcRuleCheckFailureCount} rule-check retries: {failureReason}";
            anchor.QcQualityScore = null;
            anchor.FlaggedForQcReview = true;
            // Not counted as "needsRetry" - a given-up column lands on FailedValidation, which
            // RunAsync won't touch again, so another RunBruteForce iteration would find no further
            // work for it. Only "still-retriable" resets should drive that loop's continuation.
            return (true, needsRetry: false, gaveUp: true);
        }

        Console.WriteLine($"Quality review rule check: {textFile.Path} QcTranslated now breaks the rules ({failureReason}), retry {anchor.QcRuleCheckFailureCount}/{config.QualityReview.MaxRuleCheckRetries} \n{current}");
        anchor.ResetQcState(); // Leaves QcRuleCheckFailureCount/Baseline alone - see their doc comments.
        return (true, needsRetry: true, gaveUp: false);
    }

    /// <summary>
    /// If <paramref name="anchor"/>'s current <see cref="TranslationSplit.QcQualityScore"/> is still
    /// below <see cref="Configuration.QualityReviewConfig.MinAcceptableScore"/>, wakes it up
    /// (<see cref="QcStatus.NotReviewed"/>, not fresh) for a real review rather than deciding
    /// retry-vs-give-up here itself - that decision now lives entirely in
    /// <see cref="ReviewColumnAsync"/>'s <see cref="TryRetryForLowScore"/>, computed at the point
    /// the score is actually known, so it's accurate regardless of which caller last touched this
    /// column (a plain <see cref="RunAsync"/> call or this retroactive rule-check). Deliberately
    /// does NOT touch <see cref="TranslationSplit.QcRuleCheckFailureCount"/>/<c>Baseline</c> itself
    /// when waking it up - <see cref="TryRetryForLowScore"/> will pick up wherever the count already
    /// stands against the (unchanged) <see cref="TranslationSplit.Translated"/> baseline, so a
    /// persistently low-scoring column can't get an unlimited supply of fresh starts just because
    /// this retroactive check happened to run again. If the score is already fine, clears the
    /// counter for tidiness (nothing left to track) instead. And if the count already exceeds the
    /// cap, does nothing at all - <see cref="TryRetryForLowScore"/> already gave up and accepted
    /// this column as final; waking it up again here would review -> give up -> get woken up again
    /// forever, growing the count without ever actually settling.
    /// </summary>
    private static (bool changed, bool needsRetry, bool gaveUp) WakeUpIfLowScore(LlmConfig config, TranslationSplit anchor, TextFileToSplit textFile, bool alreadyChanged)
    {
        var scoreOk = anchor.QcQualityScore is not int score || score >= config.QualityReview.MinAcceptableScore;

        if (scoreOk)
        {
            var hadFailureHistory = anchor.QcRuleCheckFailureCount != 0 || anchor.QcRuleCheckFailureBaseline != string.Empty;
            anchor.QcRuleCheckFailureCount = 0;
            anchor.QcRuleCheckFailureBaseline = string.Empty;
            return (alreadyChanged || hadFailureHistory, needsRetry: false, gaveUp: false);
        }

        // Already exceeded the retry cap? TryRetryForLowScore already tried, gave up, and accepted
        // this as final - waking it up again here would start an endless loop (wake -> review ->
        // give up again, since the score doesn't retroactively improve just because this check ran
        // - -> woken up again next pass), growing QcRuleCheckFailureCount forever instead of
        // settling. Leave it exactly as ReviewColumnAsync left it; ResetQcRetryLimits is the only
        // way back in from here.
        if (anchor.QcRuleCheckFailureCount > config.QualityReview.MaxRuleCheckRetries)
            return (alreadyChanged, needsRetry: false, gaveUp: false);

        Console.WriteLine($"Quality review rule check: {textFile.Path} still scored below minimum ({anchor.QcQualityScore} < {config.QualityReview.MinAcceptableScore}) - requesting a fresh review.");
        anchor.QcStatus = QcStatus.NotReviewed;
        anchor.QcReviewedText = string.Empty;
        return (true, needsRetry: true, gaveUp: false);
    }

    /// <summary>
    /// Manual escape hatch for <see cref="TranslationSplit.QcRuleCheckFailureCount"/>: clears every
    /// column's accumulated retry count back to 0, and gives any column currently parked at
    /// <see cref="QcStatus.FailedValidation"/> a genuinely fresh review next run (full
    /// <see cref="TranslationSplit.ResetQcState"/>, not just the counter). Use this after fixing
    /// whatever was causing a persistent rule violation (e.g. removing a false-positive bad word,
    /// loosening a glossary rule) so previously given-up-on columns get retried instead of staying
    /// parked forever - <see cref="ApplyRulesToCurrentQcTranslated"/> has no way to know on its own
    /// that a fix like that happened, since nothing about the column's own text changed.
    /// </summary>
    public static async Task ResetQcRetryLimits(string workingDirectory, TextFileToSplit[] textFiles)
    {
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory, textFiles, async (outputFile, textFile, fileLines) =>
        {
            foreach (var line in fileLines)
            {
                foreach (var columnGroup in line.Splits.GroupBy(s => s.Split))
                {
                    var anchor = columnGroup.OrderBy(s => s.SubIndex).FirstOrDefault(f => f.SubIndex == 0) ?? columnGroup.First();

                    anchor.QcRuleCheckFailureCount = 0;
                    anchor.QcRuleCheckFailureBaseline = string.Empty;

                    if (anchor.QcStatus == QcStatus.FailedValidation)
                        anchor.ResetQcState();
                }
            }

            await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
        });
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
