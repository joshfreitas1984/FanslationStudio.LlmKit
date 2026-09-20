using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// The real column identity for a split/template, matching whichever of the two mutually
    /// exclusive schemes the source file actually uses: <see cref="TranslationSplit.SplitPath"/>/
    /// <see cref="FieldTemplate.SplitPath"/> for JSON-field-path files, or <see
    /// cref="TranslationSplit.Split"/>/<see cref="FieldTemplate.Split"/> for CSV/PrefabText/
    /// DynamicStrings files that leave SplitPath empty. Grouping/matching on the bare int <c>Split</c>
    /// alone is wrong for JSON files - every field on a JSON line shares <c>Split == 0</c> (see
    /// JsonGameDataWorkflow's extraction), so it would collapse every field of the line (Name,
    /// ItemName, Desc, ...) into a single fake "column", handing one field's translation to another
    /// field's template. The "#" prefix on the int form keeps it from ever colliding with a numeric-
    /// looking SplitPath string.
    /// </summary>
    private static string ColumnKey(TranslationSplit split) =>
        string.IsNullOrEmpty(split.SplitPath) ? $"#{split.Split}" : split.SplitPath;

    /// <inheritdoc cref="ColumnKey(TranslationSplit)"/>
    private static string ColumnKey(FieldTemplate template) =>
        string.IsNullOrEmpty(template.SplitPath) ? $"#{template.Split}" : template.SplitPath;

    /// <summary>
    /// Captures from "CORRECTED:" to the end of the response (Singleline - dot matches newline),
    /// not just its first physical line - a multi-sentence correction is frequently joined with a
    /// real line break rather than SOURCE/TRANSLATION's literal "\n", and an earlier single-line-
    /// anchored version of this regex silently truncated those. <see cref="ContainsLeakedProtocolText"/>
    /// still independently guards the original leak concern this regex was narrowed to prevent. See
    /// "Postmortems" (bug #2) in `docs/quality-review-pass-architecture.md` (FanslationStudio.LlmKit).
    /// </summary>
    private static readonly Regex CorrectedLineRegex = new(@"^\s*CORRECTED:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// The literal protocol labels the QC prompt (<c>BaseQualityReviewPrompt.txt</c>, every model
    /// family) puts in front of the model - the two output-format labels it's told to produce
    /// (<c>SCORE:</c>/<c>CORRECTED:</c>) plus the two input labels it's shown and told not to repeat
    /// (<c>SOURCE (Chinese):</c>/<c>CURRENT TRANSLATION (English):</c> - see GetLlmVerdictAsync).
    /// Any of these appearing inside a parsed correction means the model echoed part of its own
    /// instructions back (the documented qwen2.5 quirk - see the correction-suffix-leak postmortem
    /// in DragonHierOverLlm's docs/plans/quality-review-pass.md) rather than producing clean
    /// translated text, regardless of which specific label leaked. <see cref="StandaloneNoneRegex"/>/
    /// <see cref="TrailingNoneRegex"/> cover the fifth, narrower case: the sentinel value
    /// <c>NONE</c> is only legitimate as the *entire* correction (meaning "no correction"); found
    /// stuck onto real text instead, it's the same kind of leak, just of the output value rather
    /// than a label.
    /// </summary>
    private static readonly string[] ProtocolLeakMarkers =
    {
        "SCORE:",
        "CORRECTED:",
        "SOURCE (CHINESE)",
        "CURRENT TRANSLATION (ENGLISH)",
    };

    /// <summary>Catches "NONE" as its own word anywhere in the text (a leading/trailing/mid-sentence
    /// sentinel separated by whitespace or punctuation, e.g. "NONE Sword Technique Power").
    /// Deliberately case-SENSITIVE (no IgnoreCase): the leaked sentinel is always the literal
    /// uppercase "NONE" copied verbatim from the prompt's format, whereas a genuine QC correction is
    /// normal-cased English prose that can legitimately contain the ordinary lowercase word "none"
    /// (e.g. "...declaring that in heaven and on earth, none but I am supreme..." - a real correction
    /// that this regex used to misidentify as a protocol leak and discard as unparseable when it was
    /// IgnoreCase).</summary>
    private static readonly Regex StandaloneNoneRegex = new(@"(?<![A-Za-z])NONE(?![A-Za-z])", RegexOptions.Compiled);

    /// <summary>
    /// Catches "NONE" glued directly onto the end of the preceding word with no separator at all -
    /// a real observed case in DragonHierOverLlm's AchievementData.csv.yaml:
    /// "Defeat more than 10 enemies in a single battle with your own handsNONE". Anchored to
    /// end-of-string only (not <see cref="StandaloneNoneRegex"/>'s both-sides word-boundary check,
    /// which this exact case fails on its left side) - safe because no real English word ends in
    /// "none", so any text ending in those four letters is this leak, never a legitimate word.
    /// Case-sensitive for the same reason as <see cref="StandaloneNoneRegex"/> - the leak is always
    /// literal uppercase "NONE".
    /// </summary>
    private static readonly Regex TrailingNoneRegex = new(@"NONE\s*$", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="correctedText"/> contains leaked QC-protocol text rather than a
    /// clean translation - see <see cref="ProtocolLeakMarkers"/>/<see cref="StandaloneNoneRegex"/>/
    /// <see cref="TrailingNoneRegex"/>. <paramref name="correctedText"/> being exactly "NONE" (the
    /// legitimate "no correction needed" sentinel) is never a leak, so it's checked and excluded
    /// first. Shared between <see cref="GetLlmVerdictAsync"/> (guards a fresh response before it's
    /// ever stored) and <see cref="ResetLeakedQcCorrections"/> (finds and repairs any already-stored
    /// value that got past an older, narrower version of this check).
    /// </summary>
    internal static bool ContainsLeakedProtocolText(string correctedText)
    {
        if (correctedText.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return false;

        // Case-SENSITIVE (Ordinal, not OrdinalIgnoreCase) for the same reason StandaloneNoneRegex/
        // TrailingNoneRegex already are: a genuine leak is always the literal uppercase label copied
        // verbatim from the prompt's own format ("SCORE:", "CORRECTED:"), whereas a real correction
        // is normal-cased English prose that can legitimately contain the same word - e.g. a UI
        // string like "New practice high score: {1} points" was misidentified as a leak and silently
        // discarded (treated as unparseable) when this was OrdinalIgnoreCase, purely because "score:"
        // happened to appear as an ordinary lowercase word in a real, correct translation.
        return ProtocolLeakMarkers.Any(marker => correctedText.Contains(marker, StringComparison.Ordinal))
            || StandaloneNoneRegex.IsMatch(correctedText)
            || TrailingNoneRegex.IsMatch(correctedText);
    }

    private sealed class QcFileState
    {
        public required TextFileToSplit TextFile { get; init; }
        public required string OutputFile { get; init; }
        public required List<TranslationLine> FileLines { get; init; }
        public required ISerializer Serializer { get; init; }
        public readonly object WriteLock = new();
        public int BufferedRecords;

        /// <summary>
        /// How many of THIS file's work items are still to be dispatched this run - set once
        /// (after exclusion/freshness-filtering/sampling decide the final work item list, so it
        /// reflects what's actually going to happen, not the whole corpus) and decremented as each
        /// of the file's items finishes, regardless of outcome (a Skipped item still counts - once
        /// nothing is left pending for a file, nothing will touch it again this run, so it's due for
        /// a flush even if it was a skip). When this hits 0, RunAsync's loop flushes the file
        /// immediately rather than waiting for every OTHER file to finish too - see the loop body.
        /// Without this, a small file that finishes early (never crosses BatchlessBuffer on its own)
        /// sits unwritten in memory for the rest of a potentially multi-hour run, just as exposed to
        /// a mid-run crash/interruption as a file still being actively reviewed.
        /// </summary>
        public int PendingItems;
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
    /// Cumulative wall-clock time spent in the two costs a run can plausibly bottleneck on, so the
    /// periodic progress log can say which one is actually responsible for a slow interval instead
    /// of leaving that to guesswork - see the progress log line in <see cref="RunAsync"/>.
    /// <see cref="LlmMs"/>/<see cref="LlmCalls"/> only count a real cache-miss LLM round trip (see
    /// where this is used inside the <see cref="ReviewLlmCache"/> factory - a cache hit just awaits
    /// the same already-timed Task and adds nothing here). <see cref="IoMs"/>/<see cref="IoWrites"/>
    /// only count an actual periodic buffered flush to disk (see the outcome != Skipped guard around
    /// it in <see cref="RunAsync"/>'s loop body) - the one-time final write-back loop at the end of
    /// <see cref="RunAsync"/> is deliberately not counted here, since it always happens exactly once
    /// regardless of how the run went and isn't part of the "what's eating this interval" question.
    /// All fields updated via <see cref="Interlocked"/> - this instance is shared/mutated
    /// concurrently across every <c>Parallel.ForEachAsync</c> worker.
    /// </summary>
    internal sealed class QcTimingStats
    {
        public long LlmMs;
        public int LlmCalls;
        public long IoMs;
        public int IoWrites;
    }

    /// <summary>
    /// The model's raw verdict for one (source text, current translation, applicable glossary
    /// prompt) triple, before any file/column-specific repair or validation is applied to it -
    /// see <see cref="ReviewCacheKey"/>/<see cref="ReviewLlmCache"/>. Internal (not private) so a
    /// consuming repo's regression tests can call <see cref="GetLlmVerdictAsync"/> directly against
    /// a known SOURCE/TRANSLATION pair without needing a corpus row (see DragonHierOverLlm's
    /// `"3g. QcOmittedSubjectRegression"` test).
    /// </summary>
    internal sealed record LlmVerdict(
        bool Success,
        int? Score,
        string? CorrectedRawMasked,
        QcDefectCategory Defect = QcDefectCategory.Unknown,
        IReadOnlyList<QcDefectFinding>? Findings = null);

    /// <summary>
    /// The single source of truth for turning one confirmed <see cref="QcDefectCategory"/> into the
    /// persisted <see cref="TranslationSplit.QcDefectCategories"/> list, used everywhere
    /// <see cref="ReviewColumnAsync"/> sets that field from a scalar category rather than a real
    /// multi-defect <see cref="QcDefectFinding"/> list. Only <see cref="QcDefectCategory.None"/> (a
    /// confirmed-clean column) collapses to empty - <see cref="QcDefectCategory.Unknown"/> and
    /// <see cref="QcDefectCategory.Uncertain"/> both mean "something is unresolved here", never
    /// "nothing was found", so both must still show up as a finding rather than being silently
    /// treated like a pass.
    /// </summary>
    private static List<QcDefectCategory> DefectCategoriesFor(QcDefectCategory category) =>
        category == QcDefectCategory.None ? [] : [category];

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
    /// Records one real HTTP round trip against <paramref name="timing"/> - shared by every actual
    /// call site inside <see cref="GetLlmVerdictAsync"/>'s retry loop and
    /// <see cref="GetVerificationVerdictAsync"/>'s single call, so <see cref="QcTimingStats.LlmCalls"/>
    /// counts real round trips (needed now that one column's review can genuinely make two - a main
    /// call and a verification call - rather than the one call it always used to be). No-op when
    /// <paramref name="timing"/> is null (the optional parameter's default, used by callers - like
    /// DragonHierOverLlm's direct-call regression tests - that don't care about run-level stats).
    /// </summary>
    private static void RecordLlmCall(QcTimingStats? timing, Stopwatch stopwatch)
    {
        if (timing == null)
            return;

        Interlocked.Add(ref timing.LlmMs, stopwatch.ElapsedMilliseconds);
        Interlocked.Increment(ref timing.LlmCalls);
    }

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
        var excludedCount = 0;
        foreach (var file in fileStates)
        {
            foreach (var line in file.FileLines)
            {
                foreach (var columnGroup in line.Splits.GroupBy(ColumnKey))
                {
                    var fragments = columnGroup.OrderBy(s => s.SubIndex).ToList();
                    var anchor = fragments.FirstOrDefault(f => f.SubIndex == 0) ?? fragments[0];
                    var template = line.Templates.FirstOrDefault(t => ColumnKey(t) == columnGroup.Key);

                    if (config.Hooks?.CustomQcExclusionRule != null)
                    {
                        var rawText = template != null
                            ? CompoundFieldSplitter.Reconstruct(template.Template, fragments.Select(f => f.Text).ToList())
                            : anchor.Text;

                        if (config.Hooks.CustomQcExclusionRule(file.TextFile, anchor.Split, rawText))
                        {
                            excludedCount++;
                            continue;
                        }
                    }

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

        if (excludedCount > 0)
            Console.WriteLine($"Quality review: {excludedCount} column(s) excluded by CustomQcExclusionRule (never became a work item).");

        // Pre-scan out anything that would just be QcOutcome.Skipped anyway (readiness/freshness -
        // see EvaluateReadiness) before it ever reaches Parallel.ForEachAsync. Purely CPU-bound, no
        // LLM call either way, so this changes nothing about which columns get reviewed - it only
        // means:
        //  - "remaining" in the progress log reflects real work left, not a count that includes
        //    thousands of already-fresh columns that will resolve in microseconds (misleading on a
        //    mostly-already-reviewed corpus - see the log this was added in response to).
        //  - sampleSize (below) draws its random sample from columns that actually need review,
        //    instead of potentially wasting sample slots on columns that would've just been skipped.
        // Safe to do once up front: nothing in this workflow makes reviewing column A change
        // column B's own readiness/freshness, so a column's answer here can't go stale mid-run.
        var alreadyFreshCount = workItems.Count;
        workItems = workItems.Where(item => EvaluateReadiness(item, config.QualityReview).needsReview).ToList();
        alreadyFreshCount -= workItems.Count;

        if (alreadyFreshCount > 0)
            Console.WriteLine($"Quality review: {alreadyFreshCount} column(s) already reviewed and unchanged (or not yet ready) - skipped without dispatching.");

        if (sampleSize is int sample && sample < workItems.Count)
        {
            // Random, not first-N - a first-N sample would be biased toward whichever file(s)
            // happen to be enumerated first (e.g. alphabetically), not representative of the mix
            // of plain/templated columns across the whole corpus.
            workItems = workItems.OrderBy(_ => Random.Shared.Next()).Take(sample).ToList();
            Console.WriteLine($"Quality review: sampling {workItems.Count} of the eligible column(s) (sampleSize={sample}).");
        }

        Console.WriteLine($"Quality review: {workItems.Count} column(s) across {fileStates.Count} file(s) to consider, max concurrency {maxConcurrency}, model '{config.QualityReview.ModelName}'.");

        // Set once the final work item list is settled (post exclusion/freshness-filter/sampling),
        // so it reflects what will actually be dispatched this run - see QcFileState.PendingItems.
        foreach (var fileGroup in workItems.GroupBy(i => i.File))
            fileGroup.Key.PendingItems = fileGroup.Count();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var reviewCache = new ReviewLlmCache();

        var totalCount = workItems.Count;
        var processedCount = 0;
        var reviewedCount = 0;
        var correctedCount = 0;
        var rejectedCount = 0;
        var flaggedCount = 0;

        // See QcTimingStats' doc comment - lets the progress log attribute an interval's real
        // wall-clock cost to "LLM calls" vs "disk flush" instead of leaving that to guesswork, which
        // is exactly what motivated adding this (a mostly-Skip run that was still slow turned out to
        // be the flush, not the LLM - see the outcome != Skipped guard below and its comment).
        var timing = new QcTimingStats();
        var runStopwatch = Stopwatch.StartNew();
        var progressLogLock = new object();
        long lastLogElapsedMs = 0, lastLogLlmMs = 0, lastLogIoMs = 0;
        int lastLogLlmCalls = 0, lastLogIoWrites = 0, lastLogReviewedCount = 0;

        await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency }, async (item, _) =>
        {
            var outcome = await ReviewColumnAsync(config, modelConfig, client, item, reviewCache, timing);

            // Every dispatched work item counts toward processedCount, Skipped included, so the
            // progress log's denominator reflects real "N left" progress rather than going quiet
            // for long stretches whenever most columns are already-fresh Skips (see
            // docs/quality-review-pass-architecture.md's "Progress logging" section).
            var processed = Interlocked.Increment(ref processedCount);

            if (outcome != QcOutcome.Skipped)
            {
                Interlocked.Increment(ref reviewedCount);
                if (outcome == QcOutcome.Corrected) Interlocked.Increment(ref correctedCount);
                if (outcome == QcOutcome.RejectedByGate) Interlocked.Increment(ref rejectedCount);
                if (item.Anchor.FlaggedForQcReview) Interlocked.Increment(ref flaggedCount);
            }

            if (processed % TranslationService.BatchlessLog == 0 || processed == totalCount)
            {
                lock (progressLogLock)
                {
                    var elapsedNow = runStopwatch.ElapsedMilliseconds;
                    var intervalMs = elapsedNow - lastLogElapsedMs;

                    var currentLlmMs = Volatile.Read(ref timing.LlmMs);
                    var currentLlmCalls = Volatile.Read(ref timing.LlmCalls);
                    var currentIoMs = Volatile.Read(ref timing.IoMs);
                    var currentIoWrites = Volatile.Read(ref timing.IoWrites);

                    // Snapshotted the same way as timing.* above, even though these are plain int
                    // fields (not long) mutated elsewhere via Interlocked.Increment from concurrent
                    // workers - Volatile.Read is what actually guarantees this thread observes the
                    // latest value rather than a stale cached one, the same guarantee the writer side
                    // gets for free from Interlocked.Increment. Using them directly (as before) is
                    // very unlikely to misbehave on .NET's current runtime, but relying on that
                    // incidental atomicity instead of an explicit acquire-style read is exactly the
                    // kind of inconsistency worth closing while touching this block.
                    var currentReviewedCount = Volatile.Read(ref reviewedCount);
                    var currentCorrectedCount = Volatile.Read(ref correctedCount);
                    var currentRejectedCount = Volatile.Read(ref rejectedCount);
                    var currentFlaggedCount = Volatile.Read(ref flaggedCount);

                    var intervalLlmMs = currentLlmMs - lastLogLlmMs;
                    var intervalLlmCalls = currentLlmCalls - lastLogLlmCalls;
                    var intervalIoMs = currentIoMs - lastLogIoMs;
                    var intervalIoWrites = currentIoWrites - lastLogIoWrites;
                    var intervalReviewed = currentReviewedCount - lastLogReviewedCount;

                    // The three numbers worth actually comparing run-to-run/setting-to-setting
                    // (raw "interval took Xms" isn't, since it moves with batch composition - a
                    // skip-heavy interval and a review-heavy one aren't comparable at all):
                    // - avgLlmMs: mean wall-clock cost of one real LLM round trip this interval -
                    //   the number to watch when judging a model/prompt-size change, independent of
                    //   concurrency.
                    // - effectiveConcurrency: intervalLlmMs / intervalMs - how many LLM calls were
                    //   genuinely running in parallel on average. Compare this to
                    //   qualityReview.maxConcurrency: at (or very near) the configured max, workers
                    //   are never sitting idle waiting on something else (io, readiness checks) -
                    //   below it means concurrency itself isn't the bottleneck right now.
                    // - reviewedPerSecond: real throughput (columns actually reviewed, not
                    //   dispatched/skipped, per second) - the one number to compare directly across
                    //   a maxConcurrency or model change to see if it actually helped.
                    var avgLlmMs = intervalLlmCalls > 0 ? intervalLlmMs / (double)intervalLlmCalls : 0;
                    var effectiveConcurrency = intervalMs > 0 ? intervalLlmMs / (double)intervalMs : 0;
                    var reviewedPerSecond = intervalMs > 0 ? intervalReviewed * 1000.0 / intervalMs : 0;

                    Console.WriteLine(
                        $"Quality review progress: {processed}/{totalCount} column(s) processed ({totalCount - processed} remaining) - " +
                        $"reviewed: {currentReviewedCount}, corrected: {currentCorrectedCount}, rejected by gate: {currentRejectedCount}, flagged: {currentFlaggedCount} | " +
                        $"interval took {intervalMs}ms - llm: {intervalLlmCalls} call(s)/{intervalLlmMs}ms, io: {intervalIoWrites} write(s)/{intervalIoMs}ms " +
                        $"(elapsed: {elapsedNow}ms) | avg {avgLlmMs:F0}ms/call, {effectiveConcurrency:F1}x effective concurrency, {reviewedPerSecond:F2} reviewed/s");

                    lastLogElapsedMs = elapsedNow;
                    lastLogLlmMs = currentLlmMs;
                    lastLogLlmCalls = currentLlmCalls;
                    lastLogIoMs = currentIoMs;
                    lastLogIoWrites = currentIoWrites;
                    lastLogReviewedCount = currentReviewedCount;
                }
            }

            // Skipped means nothing on this column changed (no LLM call, no Qc field mutated), so
            // it has nothing new to persist - counting it toward the flush threshold anyway used to
            // force a full serialize+write of the WHOLE file's FileLines on a mostly-skip run (a
            // corpus re-review where almost everything is still fresh), burning CPU/IO and stalling
            // one of only maxConcurrency worker slots on synchronous disk I/O for no reason. The
            // final write-back loop at the end of RunAsync still guarantees everything reaches disk.
            if (outcome != QcOutcome.Skipped)
            {
                var buffered = Interlocked.Increment(ref item.File.BufferedRecords);
                if (buffered > TranslationService.BatchlessBuffer)
                {
                    lock (item.File.WriteLock)
                    {
                        if (item.File.BufferedRecords > TranslationService.BatchlessBuffer)
                        {
                            var ioStopwatch = Stopwatch.StartNew();
                            FileHelper.WriteAllTextWithRetry(item.File.OutputFile, item.File.Serializer.Serialize(item.File.FileLines));
                            Interlocked.Add(ref timing.IoMs, ioStopwatch.ElapsedMilliseconds);
                            Interlocked.Increment(ref timing.IoWrites);
                            item.File.BufferedRecords = 0;
                        }
                    }
                }
            }

            // Last item dispatched for this file this run (see QcFileState.PendingItems) - nothing
            // else will touch it, so flush now instead of leaving it in memory until every OTHER
            // file's work finishes too (the final write-back loop below). Counted down regardless of
            // outcome (a Skipped last item still means the file is done) - and checked even when the
            // buffer-threshold branch above JUST flushed it, since BufferedRecords could be 0 again
            // here for an unrelated reason; a redundant write of unchanged content is harmless.
            if (Interlocked.Decrement(ref item.File.PendingItems) == 0)
            {
                lock (item.File.WriteLock)
                {
                    var ioStopwatch = Stopwatch.StartNew();
                    FileHelper.WriteAllTextWithRetry(item.File.OutputFile, item.File.Serializer.Serialize(item.File.FileLines));
                    Interlocked.Add(ref timing.IoMs, ioStopwatch.ElapsedMilliseconds);
                    Interlocked.Increment(ref timing.IoWrites);
                    item.File.BufferedRecords = 0;
                }
            }
        });

        foreach (var file in fileStates)
            await FileHelper.WriteAllTextWithRetryAsync(file.OutputFile, file.Serializer.Serialize(file.FileLines));

        Console.WriteLine($"Quality review done: {reviewedCount} reviewed, {correctedCount} corrected, {rejectedCount} rejected by validation gate, {flaggedCount} flagged for human review. " +
            $"Totals - llm: {timing.LlmCalls} call(s)/{timing.LlmMs}ms, io: {timing.IoWrites} write(s)/{timing.IoMs}ms, elapsed: {runStopwatch.ElapsedMilliseconds}ms.");

        return reviewedCount;
    }

    /// <summary>
    /// Everything <see cref="ReviewColumnAsync"/> needs to decide "does this column need an LLM
    /// call at all" - readiness (every fragment has a real, non-flagged translation) plus the
    /// freshness check (already reviewed and nothing's changed since) - with no LLM call and no
    /// side effects, so it's safe to run speculatively before a column is ever dispatched. Reused
    /// two ways: <see cref="RunAsync"/> pre-scans every work item with it purely to size the run
    /// (accurate "remaining" count, and a sampleSize draw that only picks from columns that'll
    /// actually be reviewed) without dispatching known-skips into Parallel.ForEachAsync at all;
    /// <see cref="ReviewColumnAsync"/> calls it again right before actually reviewing a column and
    /// reuses the returned <c>effectiveTranslated</c> instead of recomputing it - the pre-scan and
    /// the real pass never end up disagreeing since they're the same code path. Returns
    /// <c>effectiveTranslated</c> even when <paramref name="item"/> isn't ready (empty string) -
    /// callers that only care about <c>needsReview</c> (the pre-scan) simply ignore it.
    /// </summary>
    private static (bool needsReview, string effectiveTranslated) EvaluateReadiness(QcWorkItem item, Configuration.QualityReviewConfig qualityReview)
    {
        var anchor = item.Anchor;

        // Readiness: every fragment in this column must already have a real, non-flagged
        // translation - reviewing a column mid-retry-loop would waste a call reviewing text
        // that's about to be replaced anyway.
        foreach (var fragment in item.Fragments)
        {
            if (fragment.FlaggedForRetranslation || !fragment.SafeToTranslate)
                return (false, string.Empty);
            if (string.IsNullOrEmpty(fragment.Translated) && !string.IsNullOrEmpty(fragment.Text))
                return (false, string.Empty);
        }

        var effectiveTranslated = QualityReviewHelpers.ComputeEffectiveTranslatedText(anchor, item.Template, item.Fragments);

        if (string.IsNullOrEmpty(effectiveTranslated))
            return (false, effectiveTranslated);

        // Already reviewed and nothing has changed since (Translated is the immutable
        // source-of-truth compared here, never QcTranslated - see QcReviewedText's doc comment) -
        // skip the LLM call entirely. Same freshness check packaging uses to decide whether a
        // prior Qc* outcome can still be trusted (see QualityReviewHelpers.IsQcReviewFresh) -
        // here it means "no re-review needed" instead of "no longer trustworthy".
        if (QualityReviewHelpers.IsQcReviewFresh(anchor, item.Template, item.Fragments, qualityReview))
            return (false, effectiveTranslated);

        return (true, effectiveTranslated);
    }

    private static async Task<QcOutcome> ReviewColumnAsync(LlmConfig config, ModelExecutionConfig modelConfig, HttpClient client, QcWorkItem item, ReviewLlmCache reviewCache, QcTimingStats timing)
    {
        var anchor = item.Anchor;

        var (needsReview, effectiveTranslated) = EvaluateReadiness(item, config.QualityReview);
        if (!needsReview)
            return QcOutcome.Skipped;

        var rawText = item.Template != null
            ? CompoundFieldSplitter.Reconstruct(item.Template.Template, item.Fragments.Select(f => f.Text).ToList())
            : anchor.Text;

        // Mask dynamic tokens/placeholders the same way the translation pipeline already does
        // (StringTokenReplacer), so the QC model never sees/mangles a raw `#PlayerName#`-style
        // token or a `{n}` template slot. Restored before validating/storing any correction.
        var tokenReplacer = new StringTokenReplacer();
        var maskedRaw = tokenReplacer.Replace(rawText);
        var maskedTranslated = tokenReplacer.Replace(effectiveTranslated);

        var glossaryPrompt = GlossaryLine.AppendPromptsFor(rawText, config.Runtime.GlossaryLines, item.File.TextFile.Path);

        var cacheKey = new ReviewCacheKey(rawText, effectiveTranslated, glossaryPrompt);
        // Timed inside the Lazy factory, not around the GetOrAdd/.Value await - that way only the
        // thread that actually runs a fresh HTTP round trip (a cache miss) records time against
        // timing.LlmMs; every other thread racing the same key just awaits the same in-flight Task
        // and correctly contributes nothing (see ReviewLlmCache's doc comment for the Lazy dedup).
        // Timing/call-count instrumentation now happens per real HTTP round trip INSIDE
        // GetLlmVerdictAsync/GetVerificationVerdictAsync (see RecordLlmCall) rather than once here
        // around the whole (possibly two-call) verdict - a column that also triggers a verification
        // call genuinely makes two round trips, and timing.LlmCalls needs to reflect that instead of
        // silently bundling both into "1 call" the way a single outer stopwatch/increment would.
        var verdict = await reviewCache.GetOrAdd(cacheKey, _ => new Lazy<Task<LlmVerdict>>(
            () => GetLlmVerdictAsync(config, modelConfig, client, rawText, maskedRaw, maskedTranslated, glossaryPrompt, timing))).Value;

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
        anchor.QcDefectCategory = verdict.Defect;
        anchor.QcDefectCategories = verdict.Findings?
            .Select(finding => finding.Category)
            .Distinct()
            .ToList()
            ?? DefectCategoriesFor(verdict.Defect);

        if (verdict.CorrectedRawMasked == null)
        {
            // DEFECT: UNCERTAIN - a genuine "ask a human" signal, not a confidence score to weigh
            // against MinAcceptableScore (verdict.Score is always null for it - see
            // QcDefectCategory.Uncertain). Always flag, never retry (there's no candidate fix to
            // retry toward) and never let AutoAcceptDefectCategories silence it - PassesQcScoreGate
            // never even gets asked, since QcTranslated stays empty here just like an ordinary
            // Passed column, so packaging always falls through to the untouched Translated text.
            if (verdict.Defect == QcDefectCategory.Uncertain)
            {
                anchor.QcStatus = QcStatus.Passed;
                anchor.FlaggedForQcReview = true;
                return QcOutcome.Passed;
            }

            // Decided here, at the point the score is actually known, rather than deferred to a
            // separate step (ApplyRulesToCurrentQcTranslated) that only runs as part of
            // RunBruteForce - a plain RunAsync call must leave QcStatus just as accurate as a
            // brute-forced one, not dependent on which caller happened to invoke this.
            //
            // NOTE: a mechanical override that force-retried/flagged a DEFECT: NONE verdict whenever
            // the effective text matched a tag-seam regex (HasUnresolvedTagSeam) was tried and
            // reverted here - it can't distinguish a genuinely dangling fragment (the defect it was
            // meant to catch) from two complete, correctly-punctuated sentences that just happen to
            // be glued together with no space (completely normal, not a defect) - that distinction
            // needs actual grammatical understanding, which a regex can't provide. It also never
            // produced a real fix even for a genuine miss (the model just repeats DEFECT: NONE on
            // retry and the column ends up flagged with QcTranslated still empty, same as if nothing
            // had been done), while forcing false retries/flags on fine translations elsewhere. A
            // genuine model miss on this defect shape is inherent LLM noise (see
            // docs/quality-review-pass-architecture.md's postmortems) - triage it like any other
            // low-precision category (WriteTriageReportAsync/hand-review), don't try to force-correct
            // it in code.
            if (TryRetryForLowScore(config, anchor, effectiveTranslated, verdict.Score ?? 100, item.File.TextFile))
                return QcOutcome.Passed;

            anchor.QcStatus = QcStatus.Passed;
            anchor.FlaggedForQcReview = verdict.Score is not int passedScore || passedScore < config.QualityReview.MinAcceptableScore;
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

        // A confirmed/claimed DEFECT whose "fix" comes back byte-identical to the already-accepted
        // translation isn't a real correction - nothing to validate, nothing to flag a human with.
        // Observed in practice after two-stage verification confirms a category but the model's own
        // rewrite happens to reproduce the original text verbatim (e.g. "霓裳仙子" -> "Fairy Nishang"
        // unchanged) - see the QcTriageByDefectCategory.yaml sample analysis in
        // qc-qualityscore-noise-investigation.md (DragonHierOverLlm) this was found from. Treated
        // exactly like the model finding nothing to correct in the first place (verdict.CorrectedRawMasked
        // == null, above) rather than as a low-confidence match that still needs human review - the
        // CONSISTENCY convention (DEFECT: NONE requires SCORE 80+) is restored here explicitly since
        // the model's own SCORE/DEFECT lines were computed against a claim that didn't actually pan out.
        if (correctedResult == effectiveTranslated)
        {
            anchor.QcDefectCategory = QcDefectCategory.None;
            anchor.QcDefectCategories = DefectCategoriesFor(QcDefectCategory.None);
            anchor.QcQualityScore = 100;
            anchor.QcStatus = QcStatus.Passed;
            anchor.FlaggedForQcReview = false;
            return QcOutcome.Passed;
        }

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
                anchor.QcDefectCategory = QcDefectCategory.Unknown;
                anchor.QcDefectCategories = DefectCategoriesFor(QcDefectCategory.Unknown);
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

        // Accept immediately - do NOT re-run TryRetryForLowScore here. correctedResult has already
        // cleared the validation gate above, so a low verdict.Score is just the model's own
        // (frequently miscalibrated) confidence, not a sign the correction is wrong - re-rolling on
        // it used to discard known-good fixes for a coin-flip re-answer. See "Postmortems" (bug #3)
        // in `docs/quality-review-pass-architecture.md` (FanslationStudio.LlmKit).
        anchor.QcRuleCheckFailureCount = 0;
        anchor.QcRuleCheckFailureBaseline = string.Empty;
        anchor.QcStatus = QcStatus.Corrected;
        anchor.QcTranslated = correctedResult;
        anchor.FlaggedForQcReview = verdict.Score is not int correctedScore || correctedScore < config.QualityReview.MinAcceptableScore;
        return QcOutcome.Corrected;
    }

    /// <summary>
    /// Used only by <see cref="ReviewColumnAsync"/>'s Passed path (<c>verdict.CorrectedRawMasked ==
    /// null</c> - the model found nothing worth correcting): decides whether <paramref name="score"/>
    /// is worth another try, using the SAME retry budget (<see cref="TranslationSplit.QcRuleCheckFailureCount"/>/
    /// <see cref="Configuration.QualityReviewConfig.MaxRuleCheckRetries"/>) a rule violation gets -
    /// computed right here, at the point the score is actually known, rather than deferred to
    /// <see cref="ApplyRulesToCurrentQcTranslated"/>, a separate step that only runs as part of
    /// <see cref="RunBruteForce"/>. A plain <see cref="RunAsync"/> call must leave
    /// <see cref="TranslationSplit.QcStatus"/> just as accurate as a brute-forced one.
    ///
    /// Deliberately NOT used by the accepted-Corrected path anymore - retrying there discarded an
    /// already-validated correction for nothing but a low self-reported score (see "Postmortems"
    /// bug #3 in `docs/quality-review-pass-architecture.md`, FanslationStudio.LlmKit). A Passed
    /// column has nothing to lose by retrying here - there's no correction to discard, just a
    /// re-review of the same already-accepted <see cref="TranslationSplit.Translated"/>.
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
    /// Calls 1 and 2 both use this - a plain, independent multi-defect detection call with no
    /// knowledge of any other call's findings. Call 1 is the first invocation; call 2 is a SECOND,
    /// completely separate invocation (fresh message list, no shared history) so it can discover any
    /// issue itself rather than only confirm/deny call 1's specific claim - see
    /// <see cref="GetLlmVerdictAsync"/>, which merges both results via
    /// <see cref="QcDetectionResult.Merge"/> rather than trusting either alone.
    /// </summary>
    internal static async Task<QcDetectionResult> DetectDefectsAsync(
        LlmConfig config,
        ModelExecutionConfig modelConfig,
        HttpClient client,
        string rawText,
        string maskedRaw,
        string maskedTranslated,
        string glossaryPrompt,
        QcTimingStats? timing)
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
        var llmStopwatch = Stopwatch.StartNew();
        try
        {
            llmResponse = await TranslationService.TranslateMessagesAsync(client, config, modelConfig, messages);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            RecordLlmCall(timing, llmStopwatch);
            Console.WriteLine($"Quality review detection request error for '{rawText}': {e.Message}");
            return new QcDetectionResult(false, []);
        }
        RecordLlmCall(timing, llmStopwatch);

        var detection = QcDetectionResponseParser.Parse(llmResponse);
        if (!detection.Success)
            Console.WriteLine($"Quality review: could not parse detection response for '{rawText}' - skipping. Raw response: {llmResponse}");

        return detection;
    }

    /// <summary>
    /// Call 3 - writes ONE correction addressing every category in <paramref name="confirmedDefects"/>
    /// (the union call 1 and call 2 confirmed via <see cref="QcDetectionResult.Merge"/>), together in
    /// the same line. Never invoked with an empty/Uncertain-only list - see
    /// <see cref="GetLlmVerdictAsync"/>, which never drafts a correction when nothing concrete was
    /// confirmed. Includes the same inline "that broke the bad-words list, try again" retry budget
    /// call 1 used to have, since this is now the only call that ever drafts a correction.
    /// </summary>
    internal static async Task<string?> GenerateCorrectionAsync(
        LlmConfig config,
        ModelExecutionConfig modelConfig,
        HttpClient client,
        string rawText,
        string maskedRaw,
        string maskedTranslated,
        string glossaryPrompt,
        IReadOnlyList<QcDefectCategory> confirmedDefects,
        QcTimingStats? timing)
    {
        var defectTokens = string.Join(", ", confirmedDefects.Select(QcDefectCategoryTokens.ToToken));

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine($"SOURCE (Chinese): {maskedRaw}");
        userPrompt.AppendLine($"CURRENT TRANSLATION (English): {maskedTranslated}");
        userPrompt.AppendLine($"CONFIRMED DEFECTS: {defectTokens}");
        if (!string.IsNullOrEmpty(glossaryPrompt))
        {
            userPrompt.AppendLine("Relevant glossary terms (must be preserved if they appear in SOURCE):");
            userPrompt.AppendLine(glossaryPrompt);
        }

        var messages = new List<object>
        {
            LlmHelpers.GenerateSystemPrompt(modelConfig.Prompts["BaseQualityReviewCorrectionPrompt"]),
            LlmHelpers.GenerateUserPrompt(userPrompt.ToString()),
        };

        // Same inline self-heal budget GetLlmVerdictAsync's call-1 drafting used to have - see
        // QualityReviewConfig.InlineRuleCheckRetries's doc comment.
        var maxInlineRetries = Math.Max(0, config.QualityReview.InlineRuleCheckRetries);

        for (var attempt = 0; ; attempt++)
        {
            string llmResponse;
            var llmStopwatch = Stopwatch.StartNew();
            try
            {
                llmResponse = await TranslationService.TranslateMessagesAsync(client, config, modelConfig, messages);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                RecordLlmCall(timing, llmStopwatch);
                Console.WriteLine($"Quality review correction request error for '{rawText}': {e.Message}");
                return null;
            }
            RecordLlmCall(timing, llmStopwatch);

            var correctedMatch = CorrectedLineRegex.Match(llmResponse);
            var correctedRaw = correctedMatch.Success ? correctedMatch.Groups[1].Value.Trim() : string.Empty;

            if (ContainsLeakedProtocolText(correctedRaw))
            {
                Console.WriteLine($"Quality review: generated correction for '{rawText}' contains leaked QC-protocol text - treating as unparseable. Raw response: {llmResponse}");
                return null;
            }

            var hasCorrection = correctedMatch.Success
                && !string.IsNullOrEmpty(correctedRaw)
                && !correctedRaw.Equals("NONE", StringComparison.OrdinalIgnoreCase);

            if (!hasCorrection)
                return null;

            var badWordMatches = TranslationWorkflow.FindBadWordMatches(correctedRaw);
            if (badWordMatches.Count == 0 || attempt >= maxInlineRetries)
                return correctedRaw;

            Console.WriteLine($"Quality review: correction for '{rawText}' matched the bad-words list ({string.Join(", ", badWordMatches)}) - inline retry {attempt + 1}/{maxInlineRetries}.");
            TranslationService.AddCorrectionMessages(
                messages,
                llmResponse,
                $"That correction is not acceptable: it uses the banned word(s) \"{string.Join("\", \"", badWordMatches)}\". " +
                "Propose a different corrected line that says the same thing without using any banned word, " +
                "in the same CORRECTED: format.");
        }
    }

    /// <summary>
    /// Orchestrates the full five-call QC pass for one <see cref="ReviewCacheKey"/>. Pure with
    /// respect to any one <see cref="QcWorkItem"/> - deliberately has no knowledge of which
    /// column(s) requested it, so its result can be safely shared by every column that reduces to
    /// the same (source, current translation, glossary prompt) triple (see
    /// <see cref="ReviewLlmCache"/>). File/column-specific repair and validation happen afterward,
    /// back in <see cref="ReviewColumnAsync"/>, once per column.
    ///
    /// Calls 1 and 2 (<see cref="DetectDefectsAsync"/>) are both full, independent multi-defect
    /// detections - call 2 never sees call 1's findings, so it can't anchor on them - merged via
    /// <see cref="QcDetectionResult.Merge"/> into the confirmed defect set. An UNCERTAIN finding is
    /// never treated as "nothing found": alone, it flags the column for human review with no
    /// correction attempted; alongside a named defect, it still forces a human flag on whatever
    /// verdict the named defect(s) end up with (see the final return below).
    ///
    /// If any named defect was confirmed, call 3 (<see cref="GenerateCorrectionAsync"/>) drafts ONE
    /// correction addressing all of them, then calls 4/5 (<see cref="GetVerificationVerdictAsync"/>/
    /// <see cref="GetCorrectionRepairAsync"/>) alternate - verify against the FULL confirmed set,
    /// repair whatever's unresolved or newly introduced, verify again - up to
    /// <see cref="QualityReviewConfig.MaxScoreRepairIterations"/> times, exactly mirroring the old
    /// call 2/3 loop but over a defect collection instead of a single category.
    /// </summary>
    internal static async Task<LlmVerdict> GetLlmVerdictAsync(
        LlmConfig config,
        ModelExecutionConfig modelConfig,
        HttpClient client,
        string rawText,
        string maskedRaw,
        string maskedTranslated,
        string glossaryPrompt,
        QcTimingStats? timing = null)
    {
        var call1 = await DetectDefectsAsync(config, modelConfig, client, rawText, maskedRaw, maskedTranslated, glossaryPrompt, timing);
        if (!call1.Success)
            return new LlmVerdict(false, null, null);

        var call2 = await DetectDefectsAsync(config, modelConfig, client, rawText, maskedRaw, maskedTranslated, glossaryPrompt, timing);
        if (!call2.Success)
            return new LlmVerdict(false, null, null);

        var confirmed = QcDetectionResult.Merge(call1, call2);
        if (!confirmed.Success)
            return new LlmVerdict(false, null, null);

        if (!confirmed.HasDefects)
            return new LlmVerdict(true, 100, null, QcDefectCategory.None, confirmed.Findings);

        var isUncertain = confirmed.Findings.Any(finding => finding.Category == QcDefectCategory.Uncertain);
        var namedDefects = confirmed.Findings
            .Where(finding => finding.Category != QcDefectCategory.Uncertain)
            .Select(finding => finding.Category)
            .ToList();

        // Never collapse Uncertain into None, whether or not a named defect also came out of the
        // other detector - always flag for a human, never auto-correct on the strength of a hunch.
        List<QcDefectFinding> BuildFindings(IEnumerable<QcDefectCategory> categories) => categories
            .Select(category => new QcDefectFinding(category))
            .Concat(isUncertain ? [new QcDefectFinding(QcDefectCategory.Uncertain)] : [])
            .ToList();
        QcDefectCategory Primary(IReadOnlyList<QcDefectCategory> categories) =>
            categories.Count > 0 ? categories[0] : QcDefectCategory.Uncertain;

        if (namedDefects.Count == 0)
            return new LlmVerdict(true, null, null, QcDefectCategory.Uncertain, BuildFindings(namedDefects));

        if (!modelConfig.Prompts.ContainsKey("BaseQualityReviewCorrectionPrompt"))
        {
            Console.WriteLine($"Quality review: model has no BaseQualityReviewCorrectionPrompt to draft a correction for '{rawText}' - retrying next run.");
            return new LlmVerdict(true, 0, null, Primary(namedDefects), BuildFindings(namedDefects));
        }

        var candidate = await GenerateCorrectionAsync(config, modelConfig, client, rawText, maskedRaw, maskedTranslated, glossaryPrompt, namedDefects, timing);
        if (candidate == null)
            return new LlmVerdict(true, 0, null, Primary(namedDefects), BuildFindings(namedDefects));

        if (!modelConfig.Prompts.ContainsKey("BaseQualityReviewVerificationPrompt")
            || !modelConfig.Prompts.ContainsKey("BaseQualityReviewCorrectionRepairPrompt"))
            // No verification/repair prompt for this model - the correction is still accepted (same
            // validation gate applies downstream either way) but with Score = null, so
            // ReviewColumnAsync always flags it for human review instead of trusting it unverified.
            return new LlmVerdict(true, null, candidate, Primary(namedDefects), BuildFindings(namedDefects));

        var maxRepairAttempts = Math.Max(0, config.QualityReview.MaxScoreRepairIterations);

        for (var repairAttempt = 0; ; repairAttempt++)
        {
            // Call 4 always verifies against the FULL confirmed set, never a shrinking "still open"
            // list - so a repair attempt that regresses an already-fixed defect is caught, not
            // silently missed.
            var verification = await GetVerificationVerdictAsync(config, modelConfig, client, rawText, maskedRaw, maskedTranslated, glossaryPrompt, namedDefects, candidate, timing);
            if (!verification.Success)
                return new LlmVerdict(false, null, null);

            if (verification.Accepted)
                return new LlmVerdict(true, verification.Score, candidate, Primary(namedDefects), BuildFindings(namedDefects));

            var toFix = verification.UnresolvedDefects.Concat(verification.NewDefects).Distinct().ToList();

            if (repairAttempt >= maxRepairAttempts)
                // Repair budget exhausted - accept the last verified candidate/score as-is, same as
                // the old single-defect loop; ReviewColumnAsync's own gates (score threshold,
                // validation rules) decide whether it's actually usable.
                return new LlmVerdict(true, verification.Score, candidate, Primary(toFix), BuildFindings(toFix));

            var repaired = await GetCorrectionRepairAsync(config, modelConfig, client, rawText, maskedRaw, maskedTranslated, glossaryPrompt, toFix, candidate, timing);
            if (repaired == null)
                return new LlmVerdict(true, verification.Score, candidate, Primary(toFix), BuildFindings(toFix));

            candidate = repaired;
            // Loop back to call 4 to re-verify the NEW candidate against the full confirmed set -
            // every iteration, not just the first, so nothing ever grades a fix it (or a call in the
            // same authoring role) wrote.
        }
    }

    /// <summary>
    /// A hand-picked SOURCE/TRANSLATION pair for <see cref="ProbeReviewReasoningAsync"/>, not a real
    /// <see cref="QcWorkItem"/> - no masking/templating, just the plain text a human wants the QC
    /// model's actual reasoning on.
    /// </summary>
    public sealed record QcProbeSample(string Source, string Translation, string? GlossaryPrompt = null);

    /// <summary>Raw (unstripped, thinking included) response for one <see cref="QcProbeSample"/> -
    /// call 1's detection-only prompt, the same one <see cref="DetectDefectsAsync"/> uses in
    /// production. There is no candidate correction to verify in this diagnostic path (correction
    /// generation now needs a confirmed multi-defect union call 1/2 would normally merge, which this
    /// single-shot probe deliberately doesn't compute), so this only ever reports the detection call
    /// - see <see cref="ProbeReviewReasoningAsync"/>'s doc comment.</summary>
    public sealed record QcProbeResult(QcProbeSample Sample, string ReviewRawResponse);

    /// <summary>
    /// Diagnostic-only - never called by <see cref="RunAsync"/>/<see cref="RunBruteForce"/>. Re-runs
    /// the detection prompt (<see cref="DetectDefectsAsync"/> uses the same one in production)
    /// against hand-picked <paramref name="samples"/> with thinking mode turned back on (production
    /// leaves it off - see <see cref="LlmHelpers.GenerateLlmRequestData"/> - because the prompt
    /// explicitly forbids a reasoning preamble in its OUTPUT FORMAT). Returns the full raw response,
    /// reasoning trace included, so a human can compare what the model actually reasoned against what
    /// BaseQualityReviewPrompt.txt intended, to guide prompt/rubric wording changes rather than
    /// guessing from the terse DEFECTS output alone.
    /// </summary>
    public static async Task<List<QcProbeResult>> ProbeReviewReasoningAsync(
        string workingDirectory,
        IReadOnlyList<QcProbeSample> samples,
        GameHooks? hooks = null)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory, hooks);

        if (string.IsNullOrEmpty(config.QualityReview.ModelName)
            || !config.Runtime.Models.TryGetValue(config.QualityReview.ModelName, out var modelConfig))
        {
            throw new InvalidOperationException(
                $"QualityReview.ModelName '{config.QualityReview.ModelName}' does not match any configured model. " +
                $"Configured model names: {string.Join(", ", config.Runtime.Models.Keys)}");
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var results = new List<QcProbeResult>();

        foreach (var sample in samples)
        {
            var reviewPrompt = new StringBuilder();
            reviewPrompt.AppendLine($"SOURCE (Chinese): {sample.Source}");
            reviewPrompt.AppendLine($"CURRENT TRANSLATION (English): {sample.Translation}");
            if (!string.IsNullOrEmpty(sample.GlossaryPrompt))
            {
                reviewPrompt.AppendLine("Relevant glossary terms (must be preserved if they appear in SOURCE):");
                reviewPrompt.AppendLine(sample.GlossaryPrompt);
            }

            var reviewMessages = new List<object>
            {
                LlmHelpers.GenerateSystemPrompt(modelConfig.Prompts["BaseQualityReviewPrompt"]),
                LlmHelpers.GenerateUserPrompt(reviewPrompt.ToString()),
            };
            var reviewRaw = await TranslationService.TranslateMessagesAsync(client, config, modelConfig, reviewMessages, enableThinking: true);

            results.Add(new QcProbeResult(sample, reviewRaw));
        }

        return results;
    }

    /// <summary>
    /// Call 4 - verifies a GIVEN candidate correction (<paramref name="proposedCorrectionMasked"/>)
    /// it did not write against EVERY category in <paramref name="confirmedDefects"/>, and separately
    /// checks whether it introduces any new, unconfirmed defect. Never drafts text itself (see
    /// <see cref="QcVerificationResult"/>'s doc comment for why - a call that also writes a fix
    /// cannot grade its own confidence in it without self-preference bias). Shown the *original*
    /// TRANSLATION (never a previous repair attempt's own reasoning) plus whichever candidate is
    /// currently on the table, so repeated calls across <see cref="GetLlmVerdictAsync"/>'s repair
    /// loop always judge a fresh candidate against the same fixed baseline and the same full
    /// confirmed set - never a shrinking "still open" list, so a repair that regresses an
    /// already-fixed defect is caught, not silently missed.
    ///
    /// Returns a failed <see cref="QcVerificationResult"/> (not null) when the request errors or the
    /// response doesn't parse - <see cref="GetLlmVerdictAsync"/> treats this as "can't produce a
    /// trustworthy verdict for this column at all this run," since call 1/2 never self-score.
    /// </summary>
    internal static async Task<QcVerificationResult> GetVerificationVerdictAsync(
        LlmConfig config,
        ModelExecutionConfig modelConfig,
        HttpClient client,
        string rawText,
        string maskedRaw,
        string maskedTranslated,
        string glossaryPrompt,
        IReadOnlyList<QcDefectCategory> confirmedDefects,
        string proposedCorrectionMasked,
        QcTimingStats? timing = null)
    {
        var defectTokens = string.Join(", ", confirmedDefects.Select(QcDefectCategoryTokens.ToToken));

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine($"SOURCE (Chinese): {maskedRaw}");
        userPrompt.AppendLine($"CURRENT TRANSLATION (English): {maskedTranslated}");
        userPrompt.AppendLine($"CONFIRMED DEFECTS: {defectTokens}");
        userPrompt.AppendLine($"PROPOSED CORRECTION: {proposedCorrectionMasked}");
        if (!string.IsNullOrEmpty(glossaryPrompt))
        {
            userPrompt.AppendLine("Relevant glossary terms (must be preserved if they appear in SOURCE):");
            userPrompt.AppendLine(glossaryPrompt);
        }

        var messages = new List<object>
        {
            LlmHelpers.GenerateSystemPrompt(modelConfig.Prompts["BaseQualityReviewVerificationPrompt"]),
            LlmHelpers.GenerateUserPrompt(userPrompt.ToString()),
        };

        string llmResponse;
        var llmStopwatch = Stopwatch.StartNew();
        try
        {
            llmResponse = await TranslationService.TranslateMessagesAsync(client, config, modelConfig, messages, enableThinking: config.QualityReview.VerificationThinkingEnabled);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            RecordLlmCall(timing, llmStopwatch);
            Console.WriteLine($"Quality review verification request error for '{rawText}': {e.Message}");
            return new QcVerificationResult(false, [], [], 0);
        }
        RecordLlmCall(timing, llmStopwatch);

        var result = QcVerificationResponseParser.Parse(llmResponse, confirmedDefects);
        if (!result.Success)
            Console.WriteLine($"Quality review: could not parse verification response for '{rawText}' - treating as unscored. Raw response: {llmResponse}");

        return result;
    }

    /// <summary>
    /// Call 5 - only invoked from <see cref="GetLlmVerdictAsync"/>'s repair loop when call 4 reports
    /// an unresolved or newly-introduced defect and repair attempts remain. Given
    /// <paramref name="targetDefects"/> (whatever call 4 just reported as unresolved or new) and the
    /// current <paramref name="previousAttemptMasked"/>, writes ONE improved correction addressing
    /// all of them together - never re-derives the confirmed set, never scores, single job. The loop
    /// always sends its result back through <see cref="GetVerificationVerdictAsync"/>, verified
    /// against the FULL original confirmed set again, before trusting it (this call never grades its
    /// own rewrite).
    ///
    /// Returns null - not a distinguishable "keep trying" signal - on a request error, an unparseable
    /// response, leaked protocol text, or an explicit <c>CORRECTED: NONE</c> (call 5 couldn't improve
    /// on the previous attempt). Any of these mean the same thing to the caller: stop repairing,
    /// accept the last successfully-verified candidate.
    /// </summary>
    internal static async Task<string?> GetCorrectionRepairAsync(
        LlmConfig config,
        ModelExecutionConfig modelConfig,
        HttpClient client,
        string rawText,
        string maskedRaw,
        string maskedTranslated,
        string glossaryPrompt,
        IReadOnlyList<QcDefectCategory> targetDefects,
        string previousAttemptMasked,
        QcTimingStats? timing = null)
    {
        var defectTokens = string.Join(", ", targetDefects.Select(QcDefectCategoryTokens.ToToken));

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine($"SOURCE (Chinese): {maskedRaw}");
        userPrompt.AppendLine($"CURRENT TRANSLATION (English): {maskedTranslated}");
        userPrompt.AppendLine($"TARGET DEFECTS: {defectTokens}");
        userPrompt.AppendLine($"PREVIOUS ATTEMPT: {previousAttemptMasked}");
        if (!string.IsNullOrEmpty(glossaryPrompt))
        {
            userPrompt.AppendLine("Relevant glossary terms (must be preserved if they appear in SOURCE):");
            userPrompt.AppendLine(glossaryPrompt);
        }

        var messages = new List<object>
        {
            LlmHelpers.GenerateSystemPrompt(modelConfig.Prompts["BaseQualityReviewCorrectionRepairPrompt"]),
            LlmHelpers.GenerateUserPrompt(userPrompt.ToString()),
        };

        string llmResponse;
        var llmStopwatch = Stopwatch.StartNew();
        try
        {
            llmResponse = await TranslationService.TranslateMessagesAsync(client, config, modelConfig, messages);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            RecordLlmCall(timing, llmStopwatch);
            Console.WriteLine($"Quality review repair request error for '{rawText}': {e.Message}");
            return null;
        }
        RecordLlmCall(timing, llmStopwatch);

        var correctedMatch = CorrectedLineRegex.Match(llmResponse);
        var correctedRaw = correctedMatch.Success ? correctedMatch.Groups[1].Value.Trim() : string.Empty;
        if (ContainsLeakedProtocolText(correctedRaw))
        {
            Console.WriteLine($"Quality review: repair correction for '{rawText}' contains leaked QC-protocol text - keeping previous attempt. Raw response: {llmResponse}");
            return null;
        }

        var hasCorrection = correctedMatch.Success
            && !string.IsNullOrEmpty(correctedRaw)
            && !correctedRaw.Equals("NONE", StringComparison.OrdinalIgnoreCase);

        return hasCorrection ? correctedRaw : null;
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
                foreach (var columnGroup in line.Splits.GroupBy(ColumnKey))
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

                    var template = line.Templates.FirstOrDefault(t => ColumnKey(t) == columnGroup.Key);
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
        {
            // Structurally clean - mirrors ReviewColumnAsync's fix: a low QcQualityScore is not
            // grounds to wake an already-validated Corrected column back up (see "Postmortems"
            // bug #3, docs/quality-review-pass-architecture.md). Just tidy up the retry counters.
            var hadFailureHistory = anchor.QcRuleCheckFailureCount != 0 || anchor.QcRuleCheckFailureBaseline != string.Empty;
            anchor.QcRuleCheckFailureCount = 0;
            anchor.QcRuleCheckFailureBaseline = string.Empty;
            return (changed || hadFailureHistory, needsRetry: false, gaveUp: false);
        }

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
            anchor.QcDefectCategory = QcDefectCategory.Unknown;
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
    /// Only called for a <see cref="QcStatus.Passed"/> column now (see <see cref="ApplyRulesToQcColumn"/>'s
    /// early return and its own Corrected-column branch) - a Corrected column's already-validated
    /// QcTranslated is no longer woken up purely for a low score (see "Postmortems" bug #3,
    /// docs/quality-review-pass-architecture.md). A Passed column has nothing of its own to lose by
    /// retrying, though: if <paramref name="anchor"/>'s current
    /// <see cref="TranslationSplit.QcQualityScore"/> is still below
    /// <see cref="Configuration.QualityReviewConfig.MinAcceptableScore"/>, wakes it up
    /// (<see cref="QcStatus.NotReviewed"/>, not fresh) for a real review rather than deciding
    /// retry-vs-give-up here itself - that decision lives in <see cref="ReviewColumnAsync"/>'s
    /// <see cref="TryRetryForLowScore"/>, computed at the point the score is actually known, so it's
    /// accurate regardless of which caller last touched this column (a plain <see cref="RunAsync"/>
    /// call or this retroactive rule-check). Deliberately does NOT touch
    /// <see cref="TranslationSplit.QcRuleCheckFailureCount"/>/<c>Baseline</c> itself when waking it
    /// up - <see cref="TryRetryForLowScore"/> will pick up wherever the count already
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
                foreach (var columnGroup in line.Splits.GroupBy(ColumnKey))
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
    /// Full do-over: wipes EVERY column's Qc* state back to <see cref="QcStatus.NotReviewed"/>
    /// (<see cref="TranslationSplit.ResetQcState"/> plus <see cref="TranslationSplit.QcRuleCheckFailureCount"/>/
    /// <c>Baseline</c>), regardless of its current status - unlike <see cref="ResetQcRetryLimits"/>
    /// (only un-sticks stuck/<see cref="QcStatus.FailedValidation"/> columns) or
    /// <see cref="ResetLeakedQcCorrections"/> (only repairs corrupted stored corrections), so the
    /// next <see cref="RunAsync"/>/<see cref="RunBruteForce"/> pass reviews the entire corpus again
    /// from scratch. Intentionally separate from those two, narrower resets rather than folded into
    /// either - this is a much bigger, deliberate action (re-reviewing everything is the same
    /// many-hours job a first full run was), not something that should run as a side effect of a
    /// routine "un-stick what's stuck" pass.
    ///
    /// Use this after swapping the QC model or materially changing its prompt - <see
    /// cref="Utility.QualityReviewHelpers.IsQcReviewFresh"/> only tracks whether the underlying
    /// <see cref="TranslationSplit.Translated"/> changed, never whether the model/prompt that
    /// produced an existing <see cref="QcStatus.Passed"/>/<see cref="QcStatus.Corrected"/> verdict
    /// did, so a prompt/model change alone would otherwise never trigger a re-review of anything
    /// already reviewed. See docs/quality-review-pass-architecture.md's "Postmortems" section for
    /// the case that prompted adding this: a fix to the omitted-subject QC rule and to a low-score-
    /// discard retry bug both mean a PRIOR verdict may be less trustworthy than its stored status
    /// suggests, with nothing about the column's own text having changed to signal that.
    /// </summary>
    public static async Task ResetAllQcState(string workingDirectory, TextFileToSplit[] textFiles)
    {
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory, textFiles, async (outputFile, textFile, fileLines) =>
        {
            foreach (var line in fileLines)
            {
                foreach (var split in line.Splits)
                {
                    split.ResetQcState();
                    split.QcRuleCheckFailureCount = 0;
                    split.QcRuleCheckFailureBaseline = string.Empty;
                }
            }

            await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
        });
    }

    /// <summary>
    /// Sweeps every already-reviewed column for a stored <see cref="TranslationSplit.QcTranslated"/>
    /// or <see cref="TranslationSplit.QcRejectedCorrection"/> that contains leaked QC-protocol text
    /// (see <see cref="ContainsLeakedProtocolText"/>) - i.e. one that was accepted by an earlier,
    /// narrower version of the leak guard in <see cref="GetLlmVerdictAsync"/> (the "NONE" exact-match
    /// check let a response like "Sword Technique Power NONE" through untouched, since the whole
    /// value wasn't literally "NONE"). A full <see cref="TranslationSplit.ResetQcState"/> rather than
    /// just blanking the leaked field - a review that stored a corrupted correction is not a review
    /// worth trusting the rest of either (score, reviewed-text baseline), so the column goes back to
    /// <see cref="QcStatus.NotReviewed"/> and gets a genuinely fresh review next run, exactly like a
    /// column that failed to parse at all. Safe to run any time, including after the leak guard
    /// itself has already been fixed - a clean corpus is a no-op.
    /// </summary>
    public static async Task ResetLeakedQcCorrections(string workingDirectory, TextFileToSplit[] textFiles)
    {
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory, textFiles, async (outputFile, textFile, fileLines) =>
        {
            var resetCount = 0;

            foreach (var line in fileLines)
            {
                foreach (var split in line.Splits)
                {
                    var leaked = ContainsLeakedProtocolText(split.QcTranslated)
                        || ContainsLeakedProtocolText(split.QcRejectedCorrection);

                    if (!leaked)
                        continue;

                    Console.WriteLine($"Quality review cleanup: '{textFile.Path}' split {split.Split} had leaked QC-protocol text in QcTranslated ('{split.QcTranslated}') - resetting for re-review.");
                    split.ResetQcState();
                    resetCount++;
                }
            }

            if (resetCount > 0)
                await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
        });
    }

    /// <summary>
    /// Resets only columns whose current <see cref="TranslationSplit.QcQualityScore"/> is below
    /// <paramref name="scoreThreshold"/> (default: <c>qualityReview.minAcceptableScore</c>) back to
    /// <see cref="QcStatus.NotReviewed"/> for a genuinely fresh review - narrower than
    /// <see cref="ResetAllQcState"/> (leaves every already-accepted column with an acceptable score
    /// untouched, so a re-run only spends LLM calls on the columns actually worth another look) and
    /// unrelated to <see cref="ResetQcRetryLimits"/>/<see cref="ResetLeakedQcCorrections"/> (those
    /// un-stick a validation-gate rejection or a protocol-text leak, not a plain low score). A column
    /// with <see cref="TranslationSplit.QcFailureReason"/> set (a rejected correction -
    /// <see cref="TranslationSplit.QcQualityScore"/> is already cleared to <c>null</c> for those, see
    /// the <c>RejectedByGate</c> branch in <see cref="ReviewColumnAsync"/>) is never touched here,
    /// since <c>QcQualityScore.HasValue</c> is false for it - use <see cref="ResetQcRetryLimits"/>
    /// for those instead.
    ///
    /// Intended for the case a project changes how it judges the QC model's score - e.g. tuning
    /// <c>BaseQualityReviewPrompt.txt</c>'s scoring rubric, or switching <c>qualityReview.modelName</c>
    /// to a model that scores on a different scale - so every column currently sitting at a low score
    /// under the OLD calculation gets a genuinely fresh score under the new one, rather than keeping
    /// a stale score around indefinitely (nothing else re-triggers a review just because the scoring
    /// approach changed - see <see cref="Utility.QualityReviewHelpers.IsQcReviewFresh"/>, which only
    /// tracks whether the underlying translated text changed). <paramref name="scoreThreshold"/> can
    /// be widened past the configured <c>minAcceptableScore</c> if the change is broad enough that
    /// even comfortably-passing scores are suspect (e.g. pass 101 to reset every scored column
    /// regardless of its old score) - see <see cref="ResetAllQcState"/> instead if the goal is a full
    /// from-scratch re-review including columns that were never scored at all (a rejected correction,
    /// or a column still <see cref="QcStatus.NotReviewed"/>).
    /// </summary>
    public static async Task ResetLowScoreQcState(string workingDirectory, TextFileToSplit[] textFiles, GameHooks? hooks = null, int? scoreThreshold = null)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory, hooks);
        var threshold = scoreThreshold ?? config.QualityReview.MinAcceptableScore;
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory, textFiles, async (outputFile, textFile, fileLines) =>
        {
            var resetCount = 0;

            foreach (var line in fileLines)
            {
                foreach (var columnGroup in line.Splits.GroupBy(ColumnKey))
                {
                    var anchor = columnGroup.OrderBy(s => s.SubIndex).FirstOrDefault(f => f.SubIndex == 0) ?? columnGroup.First();

                    if (anchor.QcQualityScore is not int score || score >= threshold)
                        continue;

                    Console.WriteLine($"Quality review cleanup: '{textFile.Path}' split {anchor.Split} scored {score} (below {threshold}) under the old scoring - resetting for re-review.");
                    anchor.ResetQcState();
                    resetCount++;
                }
            }

            if (resetCount > 0)
                await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
        });
    }

    /// <summary>
    /// Resets every column currently at <see cref="QcStatus.Corrected"/> back to
    /// <see cref="QcStatus.NotReviewed"/> for a fresh review, regardless of its stored
    /// <see cref="TranslationSplit.QcQualityScore"/> - status-based counterpart to
    /// <see cref="ResetLowScoreQcState"/> (which resets by score threshold instead), for the case
    /// where the *scoring itself* changed enough that every existing <c>Corrected</c> verdict's
    /// score is suspect regardless of which side of any particular threshold it happens to land
    /// on. A widened <c>scoreThreshold</c> on <see cref="ResetLowScoreQcState"/> can't express this
    /// cleanly when previously-depressed scores get overcorrected upward by a rubric fix (they'd
    /// all sit above even a generous threshold despite being just as suspect as before) - this
    /// targets the <c>Corrected</c> population directly instead of inferring it from score. Leaves
    /// <see cref="QcStatus.Passed"/> (score was never gated the same way) and
    /// <see cref="QcStatus.FailedValidation"/>/rejected columns (use
    /// <see cref="ResetQcRetryLimits"/> for those) untouched.
    /// </summary>
    public static async Task ResetCorrectedQcState(string workingDirectory, TextFileToSplit[] textFiles)
    {
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory, textFiles, async (outputFile, textFile, fileLines) =>
        {
            var resetCount = 0;

            foreach (var line in fileLines)
            {
                foreach (var columnGroup in line.Splits.GroupBy(ColumnKey))
                {
                    var anchor = columnGroup.OrderBy(s => s.SubIndex).FirstOrDefault(f => f.SubIndex == 0) ?? columnGroup.First();

                    if (anchor.QcStatus != QcStatus.Corrected)
                        continue;

                    Console.WriteLine($"Quality review cleanup: '{textFile.Path}' split {anchor.Split} was Corrected under the old scoring - resetting for re-review.");
                    anchor.ResetQcState();
                    resetCount++;
                }
            }

            if (resetCount > 0)
                await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
        });
    }

    /// <summary>
    /// Resets every currently-flagged column (<see cref="TranslationSplit.FlaggedForQcReview"/>)
    /// whose <see cref="TranslationSplit.QcDefectCategory"/> is NOT in the configured
    /// <see cref="Configuration.QualityReviewConfig.AutoAcceptDefectCategories"/> back to
    /// <see cref="QcStatus.NotReviewed"/> for a fresh review - the DEFECT-category counterpart to
    /// <see cref="ResetLowScoreQcState"/> (which resets by score threshold instead of category). See
    /// docs/qc-qualityscore-noise-investigation.md's "stratify by DEFECT category" policy step.
    ///
    /// Two things land in this bucket every time it's run: <see cref="QcDefectCategory.Unknown"/>
    /// (a line whose response predates the DEFECT-first prompt, or otherwise failed to parse a
    /// DEFECT: line) and any category deliberately left off the auto-accept list because a human
    /// hasn't hand-validated it as low-precision yet - both need another look, not a permanent home
    /// in "flagged but never revisited". Safe to re-run as often as useful: once to sweep up
    /// <c>Unknown</c> rows left over from before DEFECT was parsed, and again any time
    /// <c>AutoAcceptDefectCategories</c> changes (widened after hand-validating another category, or
    /// a category's precision verdict is revised) to pull the newly-decided set back out of
    /// "flagged" one way or the other on the next QC run.
    /// </summary>
    public static async Task ResetNonAutoAcceptedQcState(string workingDirectory, TextFileToSplit[] textFiles, GameHooks? hooks = null)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory, hooks);
        var autoAccepted = config.QualityReview.AutoAcceptDefectCategories;
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory, textFiles, async (outputFile, textFile, fileLines) =>
        {
            var resetCount = 0;

            foreach (var line in fileLines)
            {
                foreach (var columnGroup in line.Splits.GroupBy(ColumnKey))
                {
                    var anchor = columnGroup.OrderBy(s => s.SubIndex).FirstOrDefault(f => f.SubIndex == 0) ?? columnGroup.First();

                    if (!anchor.FlaggedForQcReview || autoAccepted.Contains(anchor.QcDefectCategory))
                        continue;

                    Console.WriteLine($"Quality review cleanup: '{textFile.Path}' split {anchor.Split} defect {anchor.QcDefectCategory} not auto-accepted - resetting for re-review.");
                    anchor.ResetQcState();
                    resetCount++;
                }
            }

            if (resetCount > 0)
                await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
        });
    }

    /// <summary>
    /// Half of the tag-seam signature (see <see cref="TagSeamCapitalAfterRegex"/> for the other half,
    /// and <see cref="ResetTagSeamAffectedQcState"/> for why both must match): a sentence-terminal
    /// punctuation mark directly touching a tag opener with no whitespace between them, e.g. the
    /// "e.&lt;" in "...here.&lt;b&gt;#PosText#&lt;/b&gt;Inside". Normal prose always has a space (or
    /// nothing at all) between a sentence-ending mark and a following inline tag - this glued-together
    /// shape only happens when two independently-translated fragments were stitched with a tag between
    /// them and nothing else.
    /// </summary>
    private static readonly Regex TagSeamPunctBeforeRegex = new(@"[.!?]<", RegexOptions.Compiled);

    /// <summary>
    /// Other half of the tag-seam signature (see <see cref="TagSeamPunctBeforeRegex"/>): a tag closer
    /// directly touching a capitalized word with no whitespace between them, e.g. the "&gt;I" in
    /// "...&lt;/b&gt;Inside". On its own this is too common a false positive (an opening tag directly
    /// wrapping its own capitalized content, e.g. "&lt;b&gt;Wan&lt;/b&gt;", is completely normal) -
    /// <see cref="ResetTagSeamAffectedQcState"/> only treats a column as a tag-seam candidate when
    /// BOTH this and <see cref="TagSeamPunctBeforeRegex"/> match somewhere in the same effective text,
    /// which the "Wan" example alone never satisfies.
    /// </summary>
    private static readonly Regex TagSeamCapitalAfterRegex = new(@">[A-Z]", RegexOptions.Compiled);

    /// <summary>
    /// True if <paramref name="effectiveTranslated"/> matches BOTH <see cref="TagSeamPunctBeforeRegex"/>
    /// and <see cref="TagSeamCapitalAfterRegex"/> - the full tag-seam signature. Used as a mechanical
    /// override in <see cref="ReviewColumnAsync"/>'s no-correction path (a model returning DEFECT: NONE
    /// for a genuine instance of this shape can't be trusted on wording alone - see that call site's
    /// comment) and by <see cref="ResetTagSeamAffectedQcState"/> to find candidates for re-review.
    /// </summary>
    private static bool HasUnresolvedTagSeam(string effectiveTranslated) =>
        TagSeamPunctBeforeRegex.IsMatch(effectiveTranslated) && TagSeamCapitalAfterRegex.IsMatch(effectiveTranslated);

    /// <summary>
    /// Resets every templated column CURRENTLY at <see cref="QcStatus.Passed"/> (never one already
    /// <see cref="QcStatus.Corrected"/>/<see cref="QcStatus.FailedValidation"/>) whose effective
    /// (reconstructed) translated text matches <see cref="HasUnresolvedTagSeam"/> back to
    /// <see cref="QcStatus.NotReviewed"/> for a fresh review. Deliberately excludes
    /// <see cref="QcStatus.Corrected"/> columns even though the scan (built from each fragment's raw,
    /// pre-QC <see cref="TranslationSplit.Translated"/> via <c>ComputeEffectiveTranslatedText</c>)
    /// would match them too - <c>Translated</c> itself never changes once a column is QC-corrected, so
    /// EVERY <c>Corrected</c> column that ever fixed a tag seam would match this scan forever,
    /// pointlessly re-rolling an already-accepted, already-validated fix on every run for nothing
    /// (worse: risking losing it to model non-determinism on the fresh review - see the
    /// /investigate-qc-issue writeup that found several genuinely-fixed PlotData.csv lines reset this
    /// way before this exclusion was added). Only a <c>Passed</c> column - the shape this whole
    /// mechanism exists to catch, where the model silently missed the defect entirely - has anything to
    /// gain from a fresh look. `ReviewColumnAsync`'s own <see cref="HasUnresolvedTagSeam"/> guardrail
    /// (above) is now the primary defense for new reviews going forward; this reset is only for
    /// already-recorded `Passed` verdicts from before that guardrail existed. Cheap: a plain regex scan
    /// against the already-reconstructed effective text, no LLM call of its own, same shape as
    /// <see cref="ResetStutterAffectedQcState"/>.
    /// </summary>
    public static async Task ResetTagSeamAffectedQcState(string workingDirectory, TextFileToSplit[] textFiles)
    {
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory, textFiles, async (outputFile, textFile, fileLines) =>
        {
            var resetCount = 0;

            foreach (var line in fileLines)
            {
                foreach (var columnGroup in line.Splits.GroupBy(ColumnKey))
                {
                    var fragments = columnGroup.OrderBy(s => s.SubIndex).ToList();
                    var anchor = fragments.FirstOrDefault(f => f.SubIndex == 0) ?? fragments[0];

                    // Corrected/FailedValidation columns already went through a considered
                    // verdict - see this method's doc comment for why only Passed (a silent miss)
                    // is worth resetting here.
                    if (anchor.QcStatus != QcStatus.Passed)
                        continue;

                    var template = line.Templates.FirstOrDefault(t => ColumnKey(t) == columnGroup.Key);
                    var effectiveTranslated = QualityReviewHelpers.ComputeEffectiveTranslatedText(anchor, template, fragments);

                    if (!HasUnresolvedTagSeam(effectiveTranslated))
                        continue;

                    Console.WriteLine($"Quality review cleanup: '{textFile.Path}' split {anchor.Split} effective text contains a tag seam - resetting for re-review.");
                    anchor.ResetQcState();
                    resetCount++;
                }
            }

            if (resetCount > 0)
                await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
        });
    }

    /// <summary>
    /// Matches a Chinese stammer/stutter: a word's leading character repeated right before the full
    /// word, separated by 、, ，, or , - e.g. "思、思阁主" (stammering on 思), "你、你、你……"
    /// (stammering on 你 twice over, matched here as the first 你、你 pair). See
    /// <see cref="ResetStutterAffectedQcState"/>.
    /// </summary>
    private static readonly Regex StutterPatternRegex = new(@"(?<c>[一-鿿])[、，,]\k<c>", RegexOptions.Compiled);

    /// <summary>
    /// Resets only columns whose SOURCE contains a Chinese stammer/stutter pattern (see
    /// <see cref="StutterPatternRegex"/>) back to <see cref="QcStatus.NotReviewed"/> for a fresh
    /// review - added when <c>DROPPED_STUTTER</c> became a real DEFECT category in
    /// <c>BaseQualityReviewPrompt.txt</c> (previously stutters had no named defect to be scored
    /// against, so a dropped stutter almost always passed QC silently at a high score/DEFECT: NONE -
    /// see docs/qc-qualityscore-noise-investigation.md). Deliberately resets EVERY matching column
    /// regardless of its current status/score, unlike <see cref="ResetLowScoreQcState"/>/
    /// <see cref="ResetNonAutoAcceptedQcState"/> - a column that scored well under the old prompt is
    /// exactly the case worth re-checking here, since the old prompt had no way to score a dropped
    /// stutter low in the first place. Much cheaper than <see cref="ResetAllQcState"/>: only pays for
    /// an LLM call on columns whose SOURCE actually contains the pattern, found by a plain regex scan
    /// with no LLM call of its own, instead of re-reviewing the entire corpus to catch a narrow
    /// subset of it.
    /// </summary>
    public static async Task ResetStutterAffectedQcState(string workingDirectory, TextFileToSplit[] textFiles)
    {
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory, textFiles, async (outputFile, textFile, fileLines) =>
        {
            var resetCount = 0;

            foreach (var line in fileLines)
            {
                foreach (var columnGroup in line.Splits.GroupBy(ColumnKey))
                {
                    if (!columnGroup.Any(f => StutterPatternRegex.IsMatch(f.Text)))
                        continue;

                    var anchor = columnGroup.OrderBy(s => s.SubIndex).FirstOrDefault(f => f.SubIndex == 0) ?? columnGroup.First();

                    if (anchor.QcStatus == QcStatus.NotReviewed)
                        continue;

                    Console.WriteLine($"Quality review cleanup: '{textFile.Path}' split {anchor.Split} SOURCE contains a stutter pattern - resetting for re-review.");
                    anchor.ResetQcState();
                    resetCount++;
                }
            }

            if (resetCount > 0)
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
    public record FlaggedQcReview(string FilePath, string Text, string QcReviewedText, string QcTranslated, string? RejectedCorrection, string? Reason, int? Score, QcDefectCategory Defect = QcDefectCategory.Unknown);

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
                foreach (var columnGroup in line.Splits.GroupBy(ColumnKey))
                {
                    var fragments = columnGroup.OrderBy(s => s.SubIndex).ToList();
                    var anchor = fragments.FirstOrDefault(f => f.SubIndex == 0) ?? fragments[0];

                    if (!anchor.FlaggedForQcReview)
                        continue;

                    var template = line.Templates.FirstOrDefault(t => ColumnKey(t) == columnGroup.Key);
                    var rawText = template != null
                        ? CompoundFieldSplitter.Reconstruct(template.Template, fragments.Select(f => f.Text).ToList())
                        : anchor.Text;

                    flagged.Add(new FlaggedQcReview(
                        textFile.Path,
                        rawText,
                        anchor.QcReviewedText,
                        anchor.QcTranslated,
                        string.IsNullOrEmpty(anchor.QcRejectedCorrection) ? null : anchor.QcRejectedCorrection,
                        string.IsNullOrEmpty(anchor.QcFailureReason) ? null : anchor.QcFailureReason,
                        anchor.QcQualityScore,
                        anchor.QcDefectCategory));
                }
            }

            await Task.CompletedTask;
        });

        return flagged;
    }

    /// <summary>
    /// One root-cause group within <see cref="QcTriageResult.ByReason"/> - every
    /// <see cref="FlaggedQcReview"/> whose <see cref="FlaggedQcReview.Reason"/> (a rejected
    /// correction - see <see cref="QcTriageResult"/>) is the same literal string, most-common
    /// group first. <see cref="Count"/> is the group's TOTAL size, independent of how many
    /// <see cref="Examples"/> were actually kept (capped by <c>examplesPerCluster</c> on
    /// <see cref="GetQcTriageAsync"/>) - so a consumer always knows how big the cluster really is
    /// even when only a handful of examples are shown.
    /// </summary>
    public sealed record QcTriageReasonCluster(string Reason, int Count, List<FlaggedQcReview> Examples);

    /// <summary>
    /// Every flagged row sharing the same <see cref="FlaggedQcReview.Defect"/> category (see
    /// <see cref="QcDefectCategory"/>), for the "stratify by DEFECT category, not score"
    /// triage plan - see docs/qc-qualityscore-noise-investigation.md (Tests project,
    /// DragonHierOverLlm repo). <see cref="Count"/> is the category's TOTAL size across every
    /// flagged row, independent of <see cref="Sample"/>'s cap - a human hand-validates just the
    /// sample to estimate that category's precision, then decides a blanket accept/review policy
    /// for the whole category based on it (see <see cref="QcTriageResult.ByDefectCategory"/>).
    /// </summary>
    public sealed record QcDefectCategoryCluster(QcDefectCategory Defect, int Count, List<FlaggedQcReview> Sample);

    /// <summary>
    /// <see cref="GetFlaggedQcReviews"/>'s ~thousands-of-rows output split into the two populations
    /// that need different treatment, so a human (or a chat with an LLM) can work a large flagged
    /// set down over time instead of reading every row or hand-marking individual lines "ok" (the
    /// data model deliberately has no such field - see <see cref="ReviewColumnAsync"/>'s "Auto-apply,
    /// gated by validation, not report-only" design):
    /// <list type="bullet">
    /// <item><see cref="ByReason"/> - rows with <see cref="FlaggedQcReview.Reason"/> set, meaning QC
    /// proposed a correction that failed structural validation or a glossary-drift check and was
    /// never applied. These cluster hard around a handful of root causes (a bad-word false positive,
    /// a missing glossary term, the QC prompt ignoring an instruction) - grouped by the exact reason
    /// string so fixing one root cause (prompt wording, a glossary rule, a bad-word entry) clears a
    /// whole cluster at once via <see cref="ResetQcRetryLimits"/>/<see cref="ResetLeakedQcCorrections"/>
    /// + a re-run, not one row at a time.</item>
    /// <item><see cref="LowScoreSample"/> - rows with no <see cref="FlaggedQcReview.Reason"/>, just
    /// <see cref="FlaggedQcReview.Score"/> below <paramref name="minAcceptableScore"/>-equivalent
    /// (<see cref="MinAcceptableScore"/>). The correction (if any) already passed validation and was
    /// applied - this is purely QC's own self-rated confidence. A small, deterministic
    /// (lowest-score-first, capped per file so one noisy file can't crowd out the rest) sample rather
    /// than every such row, so a human can spot-check whether the score is trustworthy and tune
    /// <c>qualityReview.minAcceptableScore</c> or the QC prompt's scoring rubric accordingly.</item>
    /// </list>
    /// Cheap and side-effect-free (no LLM calls, just re-reads what <see cref="GetFlaggedQcReviews"/>
    /// already computes) - safe to re-run as often as useful, e.g. after every prompt/glossary/config
    /// fix, to see clusters shrink and the low-score sample shift as real progress is made.
    /// </summary>
    public sealed record QcTriageResult(
        int TotalFlagged,
        int RejectedCount,
        int LowScoreOnlyCount,
        int MinAcceptableScore,
        List<QcTriageReasonCluster> ByReason,
        List<FlaggedQcReview> LowScoreSample,
        List<QcDefectCategoryCluster> ByDefectCategory);

    /// <summary>
    /// Builds <see cref="QcTriageResult"/> from <see cref="GetFlaggedQcReviews"/>'s output - see that
    /// record's doc comment for what the two populations mean and why they need different treatment.
    /// Pure data (no file I/O beyond the read <see cref="GetFlaggedQcReviews"/> itself does) - see
    /// <see cref="WriteTriageReportAsync"/>/<see cref="WriteFixPromptsAsync"/> for turnkey
    /// per-project reporting steps built on top of this.
    /// </summary>
    /// <param name="examplesPerCluster">How many example rows to keep per reason cluster - the
    /// cluster's real <see cref="QcTriageReasonCluster.Count"/> is unaffected by this cap.</param>
    /// <param name="lowScoreSampleSize">Total number of low-score-only rows to sample across all
    /// files, lowest score first.</param>
    /// <param name="maxLowScorePerFile">Caps how many of the sample can come from any one file, so a
    /// single noisy file can't crowd out every other file's rows from the sample.</param>
    /// <param name="defectCategorySampleSize">How many rows to sample per <see cref="QcDefectCategory"/>
    /// for <see cref="QcTriageResult.ByDefectCategory"/> - large enough to estimate that category's
    /// precision with a usable margin, small enough to hand-validate in one sitting. See
    /// docs/qc-qualityscore-noise-investigation.md's "stratify by DEFECT category" plan.</param>
    public static async Task<QcTriageResult> GetQcTriageAsync(
        string workingDirectory,
        TextFileToSplit[] textFiles,
        GameHooks? hooks = null,
        int examplesPerCluster = 5,
        int lowScoreSampleSize = 100,
        int maxLowScorePerFile = 15,
        int defectCategorySampleSize = 40)
    {
        var flagged = await GetFlaggedQcReviews(workingDirectory, textFiles);
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory, hooks);
        var minAcceptableScore = config.QualityReview.MinAcceptableScore;

        var rejected = flagged.Where(f => !string.IsNullOrWhiteSpace(f.Reason)).ToList();
        var lowScoreOnly = flagged
            .Where(f => string.IsNullOrWhiteSpace(f.Reason) && f.Score.HasValue && f.Score < minAcceptableScore)
            .ToList();

        var byReason = rejected
            .GroupBy(f => f.Reason!.Trim())
            .OrderByDescending(g => g.Count())
            .Select(g => new QcTriageReasonCluster(g.Key, g.Count(), g.Take(examplesPerCluster).ToList()))
            .ToList();

        var lowScoreSample = lowScoreOnly
            .OrderBy(f => f.Score)
            .GroupBy(f => f.FilePath)
            .SelectMany(g => g.Take(maxLowScorePerFile))
            .OrderBy(f => f.Score)
            .Take(lowScoreSampleSize)
            .ToList();

        // Fixed seed, not a fresh Random per call: makes the sample reproducible across repeated
        // triage runs against the same flagged set (e.g. re-running WriteTriageReportAsync after a
        // prompt tweak with no new QC pass in between) instead of a different sample every time,
        // which would make "did precision improve" impossible to tell apart from "different rows got
        // sampled". Order-shuffle rather than a per-item random skip - simplest way to get an
        // unbiased sample per category out of LINQ without a manual reservoir-sampling loop.
        var shuffleRandom = new Random(12345);
        var byDefectCategory = flagged
            .GroupBy(f => f.Defect)
            .OrderByDescending(g => g.Count())
            .Select(g => new QcDefectCategoryCluster(
                g.Key,
                g.Count(),
                g.OrderBy(_ => shuffleRandom.Next()).Take(defectCategorySampleSize).ToList()))
            .ToList();

        return new QcTriageResult(flagged.Count, rejected.Count, lowScoreOnly.Count, minAcceptableScore, byReason, lowScoreSample, byDefectCategory);
    }

    /// <summary>
    /// Turnkey per-project reporting step built on <see cref="GetQcTriageAsync"/>: writes
    /// <c>TestResults/QcTriageSummary.yaml</c> (headline counts, including a per-category count
    /// breakdown), <c>QcTriageByReason.yaml</c> (every reason cluster with its examples),
    /// <c>QcTriageLowScoreSample.yaml</c> (the low-score spot-check sample), and
    /// <c>QcTriageByDefectCategory.yaml</c> (every <see cref="QcDefectCategory"/> with its total
    /// count and a hand-validation sample - see docs/qc-qualityscore-noise-investigation.md, Tests
    /// project, DragonHierOverLlm repo, for the "stratify by DEFECT category" triage plan this
    /// feeds). Intended to be wired into a consuming repo's own workflow test file as a one-line
    /// wrapper run right after that repo's own "find flagged" step - see
    /// docs/quality-review-pass-architecture.md's "Wiring this into a new project" section.
    /// </summary>
    public static async Task WriteTriageReportAsync(string workingDirectory, TextFileToSplit[] textFiles, GameHooks? hooks = null)
    {
        var triage = await GetQcTriageAsync(workingDirectory, textFiles, hooks);

        var summary = new
        {
            triage.TotalFlagged,
            triage.RejectedCount,
            triage.LowScoreOnlyCount,
            distinctReasonClusters = triage.ByReason.Count,
            triage.MinAcceptableScore,
            // Just the counts per category, not the samples - the full breakdown (with samples) is
            // in QcTriageByDefectCategory.yaml; this is only here so the counts show up in the
            // one-glance summary/console output alongside the rest of the headline numbers.
            defectCategoryCounts = triage.ByDefectCategory.ToDictionary(c => c.Defect.ToString(), c => c.Count),
        };

        var serializer = YamlHelper.CreateSerializer();
        FileHelper.WriteAllTextWithRetry($"{workingDirectory}/TestResults/QcTriageSummary.yaml", serializer.Serialize(summary));
        FileHelper.WriteAllTextWithRetry($"{workingDirectory}/TestResults/QcTriageByReason.yaml", serializer.Serialize(triage.ByReason));
        FileHelper.WriteAllTextWithRetry($"{workingDirectory}/TestResults/QcTriageLowScoreSample.yaml", serializer.Serialize(triage.LowScoreSample));
        FileHelper.WriteAllTextWithRetry($"{workingDirectory}/TestResults/QcTriageByDefectCategory.yaml", serializer.Serialize(triage.ByDefectCategory));

        Console.WriteLine(serializer.Serialize(summary));
    }

    /// <summary>
    /// Turnkey per-project reporting step built on <see cref="GetQcTriageAsync"/>: writes
    /// <c>TestResults/QcTriagePrompts.md</c>, one markdown section per reason cluster (with a
    /// templated root-cause diagnosis request and concrete examples) plus one section for the
    /// low-score calibration sample. Deliberately generates a prompt to paste into a chat rather than
    /// calling an LLM itself - the actual fixes (QC prompt wording, a glossary rule,
    /// <c>qualityReview.minAcceptableScore</c>) are small and judgment-heavy enough that a human
    /// should read the examples and apply the change themselves rather than have an LLM edit prompt
    /// files unsupervised. See docs/quality-review-pass-architecture.md's "Wiring this into a new
    /// project" section for how to call this from a consuming repo.
    /// </summary>
    public static async Task WriteFixPromptsAsync(string workingDirectory, TextFileToSplit[] textFiles, GameHooks? hooks = null, int examplesPerCluster = 8)
    {
        var triage = await GetQcTriageAsync(workingDirectory, textFiles, hooks, examplesPerCluster: examplesPerCluster);

        var doc = new StringBuilder();
        doc.AppendLine("# Quality Review Fix Prompts");
        doc.AppendLine();
        doc.AppendLine("Generated by QualityReviewWorkflow.WriteFixPromptsAsync. Paste one section below into a");
        doc.AppendLine("Claude chat at a time, apply whatever fix it suggests by hand (the QC prompt's wording, a");
        doc.AppendLine("glossary rule, minAcceptableScore), then re-run ResetQcRetryLimits /");
        doc.AppendLine("ResetLeakedQcCorrections as appropriate, re-run the QC pass, and re-run this triage to");
        doc.AppendLine("confirm the cluster shrank.");
        doc.AppendLine();

        foreach (var group in triage.ByReason)
        {
            doc.AppendLine($"## Cluster: \"{group.Reason}\" ({group.Count} occurrences)");
            doc.AppendLine();
            doc.AppendLine("This is a quality-review pass over a machine-translated game localization corpus. The QC");
            doc.AppendLine($"model proposed a correction for each line below, but every one was rejected for the same");
            doc.AppendLine($"reason: \"{group.Reason}\". Diagnose the likely root cause (a QC prompt instruction being");
            doc.AppendLine("ignored, a missing/wrong glossary term, or an overly-broad bad-word/rule match) and suggest");
            doc.AppendLine("a specific, minimal wording change (to the QC prompt or a glossary rule) that would fix");
            doc.AppendLine("this cluster without introducing new false rejections elsewhere.");
            doc.AppendLine();
            foreach (var f in group.Examples)
            {
                doc.AppendLine($"- File: {f.FilePath}");
                doc.AppendLine($"  Raw: {f.Text}");
                doc.AppendLine($"  Kept translation: {f.QcTranslated}");
                doc.AppendLine($"  Rejected correction: {f.RejectedCorrection}");
            }
            doc.AppendLine();
        }

        if (triage.LowScoreSample.Count > 0)
        {
            doc.AppendLine($"## Low-score calibration sample ({triage.LowScoreSample.Count} lines, minAcceptableScore={triage.MinAcceptableScore})");
            doc.AppendLine();
            doc.AppendLine("These lines were auto-corrected and already passed validation, but QC's self-rated");
            doc.AppendLine($"confidence scored them below the configured threshold ({triage.MinAcceptableScore}). For each,");
            doc.AppendLine("judge whether the low score reflects a genuine translation problem or overly harsh");
            doc.AppendLine("self-rating on short/idiomatic phrases. If a pattern emerges across several, suggest a");
            doc.AppendLine("tweak to the QC prompt's scoring rubric; if they look fine, that's a signal");
            doc.AppendLine("minAcceptableScore itself could be lowered instead.");
            doc.AppendLine();
            foreach (var f in triage.LowScoreSample)
            {
                doc.AppendLine($"- File: {f.FilePath}");
                doc.AppendLine($"  Raw: {f.Text}");
                doc.AppendLine($"  Applied translation: {f.QcTranslated}");
                doc.AppendLine($"  Score: {f.Score}");
            }
            doc.AppendLine();
        }

        FileHelper.WriteAllTextWithRetry($"{workingDirectory}/TestResults/QcTriagePrompts.md", doc.ToString());
    }
}
