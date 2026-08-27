using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit;

public static class TranslationService
{
    public const int BatchlessLog = 25;
    public const int BatchlessBuffer = 25;

    // ASCII punctuation marks (post-PrepareRaw, which already normalizes their full-width forms
    // e.g. '：' -> ':') that only ever show up as the very first character of a raw split as a
    // leftover field-templating/compound-column artifact (e.g. a compound cell whose label half
    // was already carved off elsewhere, leaving "：description" behind) - never as meaningful
    // natural-language punctuation at the start of a Chinese sentence. Forcing the LLM to preserve
    // a bare leading separator like this in fluent English is unnatural, so it reliably drops it -
    // which then fails CheckTransalationSuccessful's corresponding heuristic and burns a full
    // retry budget for a mark we can attach ourselves deterministically instead. See the strip and
    // re-attach block in TranslateSplitAsync below.
    private static readonly HashSet<char> LeadingStructuralPunctuation = [':', ';', ','];

    // Cap on how long a string can be to be kept in the run-wide translation cache (see
    // TranslateViaLlmAsyncBatched/Pooled) - short/medium repeated strings (names, common short
    // phrases) are very likely to recur elsewhere and are cheap to keep in memory, but longer
    // translations (40-50+ chars) are unlikely to repeat verbatim and aren't worth retaining.
    public const int TranslationCacheMaxChars = 50;

    // Diagnostic-only counter of extra LLM round-trips spent on retries/corrections (whole-cell
    // retries in the loop below, plus each sentence-by-sentence correction round) - not scoped to a
    // single run, so callers that want a per-run delta (see TranslateViaLlmAsyncPooled's progress
    // logging) should snapshot it at the start of the run and diff against later reads.
    private static int _retryAttemptCounter;

    // Same as _retryAttemptCounter above, but scoped to attempts made against
    // LlmConfig.EscalationModelName specifically (see AttemptTranslationWithRetriesAsync's
    // isEscalation parameter in TranslateSplitAsync) - lets a run's progress log distinguish
    // "normal retries against the primary model" from "extra attempts spent escalating to a second
    // model", so escalation's actual cost/benefit is visible instead of folded into one number.
    private static int _escalationAttemptCounter;

    /// <summary>
    /// Builds <see cref="RuntimeValues.FileRestrictedEntriesByText"/> once per run - see that
    /// property's doc comment. Called once from <see cref="FillTranslationCacheAsync"/>
    /// (itself only called once per run via PrepareTranslationRunAsync), never per-split.
    /// </summary>
    private static void BuildFileRestrictedIndex(LlmConfig config)
    {
        var index = new Dictionary<string, List<GlossaryLine>>();

        void AddKey(string? key, GlossaryLine line)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (!index.TryGetValue(key, out var entries))
                index[key] = entries = [];

            entries.Add(line);
        }

        void AddIfRestricted(GlossaryLine line)
        {
            if (!line.IsFileRestricted)
                return;

            AddKey(line.Raw, line);
            AddKey(line.RawSimplified, line);
            AddKey(line.RawTraditional, line);
        }

        foreach (var line in config.Runtime.GlossaryLines)
            AddIfRestricted(line);

        foreach (var line in config.Runtime.ManualTranslations)
            AddIfRestricted(line);

        config.Runtime.FileRestrictedEntriesByText = index;
    }

    /// <summary>
    /// True if <paramref name="text"/> exactly matches a glossary or manual-translation entry that
    /// is scoped to specific output files ("only"/"exclude" - see
    /// <see cref="GlossaryLine.IsFileRestricted"/>). Such entries must never be read from or
    /// written into the run-wide <see cref="translationCache"/>: that cache is a single flat
    /// Raw->Result map shared by every output file, so caching a file-scoped translation would
    /// silently apply it to every other file too. Splits that match a restricted entry always go
    /// through the normal LLM/glossary-prompt path (or a direct manual-translation override
    /// elsewhere) instead of the shared cache.
    /// O(1) dictionary lookup against <see cref="RuntimeValues.FileRestrictedEntriesByText"/> -
    /// this is called once or twice per split, across potentially thousands of splits and many
    /// parallel workers, so it must not re-scan the glossary/manual lists each time.
    /// </summary>
    private static bool IsFileRestrictedText(string text, LlmConfig config) =>
        config.Runtime.FileRestrictedEntriesByText.ContainsKey(text);

    /// <summary>
    /// If <paramref name="text"/> exactly matches a glossary or manual-translation entry whose
    /// "only" list includes <paramref name="filePath"/>, returns that entry's Result directly -
    /// deliberately bypassing both the LLM and the shared translationCache (see
    /// IsFileRestrictedText). This lets an "only"-scoped entry act as a fixed, deterministic
    /// override for exactly the file(s) it names, without spending an LLM call or leaking through
    /// the run-wide cache into other files. O(1) dictionary lookup, same rationale as
    /// IsFileRestrictedText above.
    /// </summary>
    private static bool TryGetOnlyFileDirectTranslation(string text, string filePath, LlmConfig config, out string result)
    {
        result = string.Empty;

        if (!config.Runtime.FileRestrictedEntriesByText.TryGetValue(text, out var entries))
            return false;

        foreach (var entry in entries)
        {
            if (entry.OnlyOutputFiles.Count > 0 && entry.OnlyOutputFiles.Contains(filePath))
            {
                result = entry.Result;
                return true;
            }
        }

        return false;
    }

    public static async Task FillTranslationCacheAsync(string workingDirectory,
        int charsToCache, ConcurrentDictionary<string, string> cache,
        LlmConfig config, TextFileToSplit[] textFiles)
    {
        // Build the O(1) file-restriction lookup once per run before any splits are processed -
        // see BuildFileRestrictedIndex/RuntimeValues.FileRestrictedEntriesByText.
        BuildFileRestrictedIndex(config);

        // Add Manual adjustments
        //
        // Skip entries scoped via "only"/"exclude" - see IsFileRestrictedText above.
        foreach (var k in config.Runtime.ManualTranslations)
        {
            if (k.IsFileRestricted)
                continue;

            cache.TryAdd(k.Raw, k.Result);
        }

        // Add Glossary Lines to Cache
        //
        // The cache below is a single flat Raw->Result dictionary shared across every output
        // file in the run (see TranslateViaLlmAsyncBatched/Pooled's cacheHit lookups), so it has
        // no way to scope a hit to a specific file. A glossary line restricted via "only:" (see
        // GlossaryLine.OnlyOutputFiles) is meant to apply exclusively to those listed output
        // files - caching it here would let it silently satisfy translations for every other
        // file too, corrupting unrelated files' output. Only cache glossary lines with no file
        // restriction at all.
        foreach (var line in config.Runtime.GlossaryLines)
        {
            if (line.IsFileRestricted)
                continue;

            cache.TryAdd(line.Raw, line.Result);
        }


        // File with old files
        var oldFolder = $"{workingDirectory}/TestResults/OldFiles";

        var deserializer = YamlHelper.CreateDeserializer();

        foreach (var file in Directory.EnumerateFiles(oldFolder))
        {
            var content = File.ReadAllText(file);
            var lines = deserializer.Deserialize<List<TranslationLine>>(content);

            foreach (var line in lines)
            {
                foreach (var split in line.Splits)
                {
                    // Skip splits whose text matches a file-restricted glossary/manual entry -
                    // see IsFileRestrictedText.
                    if (IsFileRestrictedText(split.Text, config))
                        continue;

                    cache.TryAdd(split.Text, split.Translated);
                }
            }
        }

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory,
            textFiles,
            async (outputFile, textFileToTranslate, fileLines) =>
        {
            foreach (var line in fileLines)
            {
                foreach (var split in line.Splits)
                {
                    if (string.IsNullOrEmpty(split.Translated) || split.FlaggedForRetranslation)
                        continue;

                    // Skip splits whose text matches a file-restricted glossary/manual entry -
                    // see IsFileRestrictedText.
                    if (IsFileRestrictedText(split.Text, config))
                        continue;

                    if (split.Text.Length <= charsToCache)
                        cache.TryAdd(split.Text, split.Translated);
                }
            }

            await Task.CompletedTask;
        });

        //Add it to config to make it easier to use
        config.Runtime.TranslationCache = cache;
    }

    /// <summary>
    /// Loads config and the run-wide translation cache once, shared by both
    /// <see cref="TranslateViaLlmAsyncBatched"/> and <see cref="TranslateViaLlmAsyncPooled"/>.
    /// Caller owns the returned <see cref="HttpClient"/> and must dispose it.
    /// </summary>
    private static async Task<(LlmConfig Config, ConcurrentDictionary<string, string> Cache, HttpClient Client)> PrepareTranslationRunAsync(
        string workingDirectory, TextFileToSplit[] textFiles)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory);

        // Translation Cache - dedups repeated strings within this run and across history
        // (manual translations, glossary, TestResults/OldFiles, already-translated splits).
        // ConcurrentDictionary because splits are translated in parallel (both schedulers) and
        // each worker reads/writes this cache concurrently.
        var translationCache = new ConcurrentDictionary<string, string>();
        await FillTranslationCacheAsync(workingDirectory, TranslationCacheMaxChars, translationCache, config, textFiles);

        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(300)
        };

        return (config, translationCache, client);
    }

    /// <summary>
    /// Entry point used by <see cref="Workflow.TranslationWorkflow"/> - dispatches to whichever
    /// scheduler is selected by <see cref="LlmConfig.UseContinuousWorkerPool"/>. See
    /// docs/OPTIMIZATION_PLAN.md (FanslationStudio.LlmKit repo) for why both schedulers exist
    /// side by side during the transition.
    /// </summary>
    public static async Task TranslateViaLlmAsync(string workingDirectory, bool forceRetranslation,
        TextFileToSplit[] textFiles)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory);

        if (config.UseContinuousWorkerPool)
            await TranslateViaLlmAsyncPooled(workingDirectory, forceRetranslation, textFiles);
        else
            await TranslateViaLlmAsyncBatched(workingDirectory, forceRetranslation, textFiles);
    }

    /// <summary>
    /// Original scheduler: files are processed one at a time, and within a file, fixed-size
    /// batches (<see cref="LlmConfig.BatchSize"/>) are processed one at a time with a hard
    /// barrier - the next batch cannot start until every unique split in the current batch
    /// (including any retries/corrections) has finished. See docs/OPTIMIZATION_PLAN.md item #1
    /// for the tail-latency problem this causes and why <see cref="TranslateViaLlmAsyncPooled"/>
    /// exists as an alternative.
    /// </summary>
    public static async Task TranslateViaLlmAsyncBatched(string workingDirectory, bool forceRetranslation,
        TextFileToSplit[] textFiles)
    {
        string inputPath = $"{workingDirectory}/Raw/Export";
        string outputPath = $"{workingDirectory}/Converted";

        // Create output folder
        if (!Directory.Exists(outputPath))
            Directory.CreateDirectory(outputPath);

        var (config, translationCache, client) = await PrepareTranslationRunAsync(workingDirectory, textFiles);
        using var _ = client;
        var charsToCache = TranslationCacheMaxChars;

        int incorrectLineCount = 0;
        int totalRecordsProcessed = 0;

        foreach (var textFileToTranslate in textFiles)
        {
            var inputFile = $"{inputPath}/{textFileToTranslate.Path}";
            var outputFile = $"{outputPath}/{textFileToTranslate.Path}.yaml";

            if (!File.Exists(outputFile))
                File.Copy(inputFile, outputFile);

            var content = File.ReadAllText(outputFile);

            Console.WriteLine($"Processing File: {textFileToTranslate.Path}");

            var serializer = YamlHelper.CreateSerializer();
            var deserializer = YamlHelper.CreateDeserializer();
            var fileLines = deserializer.Deserialize<List<TranslationLine>>(content);

            var batchSize = config.BatchSize ?? 20;
            var totalLines = fileLines.Count;
            var stopWatch = Stopwatch.StartNew();
            int recordsProcessed = 0;
            int bufferedRecords = 0;

            int logProcessed = 0;

            for (int i = 0; i < totalLines; i += batchSize)
            {
                int batchRange = Math.Min(batchSize, totalLines - i);

                // Use a slice of the list directly
                var batch = fileLines.GetRange(i, batchRange);

                // Get Unique splits incase the batch has the same entry multiple times (eg. NPC Names)
                var uniqueSplits = batch.SelectMany(line => line.Splits)
                    .GroupBy(split => split.Text)
                    .Select(group => group.First())
                    .ToList(); // Materialize to prevent multiple enumerations;

                // Process the unique in parallel
                await Task.WhenAll(uniqueSplits.Select(async split =>
                {
                    if (string.IsNullOrEmpty(split.Text) || !split.SafeToTranslate)
                        return;

                    // File-restricted glossary/manual entries (only:/exclude:) must never be read
                    // from or written into this shared cache - see IsFileRestrictedText.
                    var isFileRestricted = IsFileRestrictedText(split.Text, config);

                    var cacheHit = !isFileRestricted
                        && translationCache.ContainsKey(split.Text)
                        // We use this for name files etc which will be in cache
                        && textFileToTranslate.EnableGlossary;

                    if (string.IsNullOrEmpty(split.Translated)
                        || forceRetranslation
                        || (config.TranslateFlagged && split.FlaggedForRetranslation))
                    {
                        var original = split.Translated;

                        if (cacheHit)
                            split.Translated = translationCache[split.Text];
                        else if (TryGetOnlyFileDirectTranslation(split.Text, textFileToTranslate.Path, config, out var directResult))
                            // Exact "only" match for this file - use it directly, no LLM call.
                            split.Translated = directResult;
                        else
                        {
                            var result = await TranslateSplitAsync(config, split.Text, client, textFileToTranslate, column: split.Split);
                            split.Translated = result.Valid ? result.Result : string.Empty;
                        }

                        split.ResetFlags(split.Translated != original);
                        recordsProcessed++;
                        totalRecordsProcessed++;
                        bufferedRecords++;
                    }

                    if (string.IsNullOrEmpty(split.Translated))
                        incorrectLineCount++;
                    else
                    {
                        //Two translations could be doing this at the same time
                        if (!cacheHit && !isFileRestricted && split.Text.Length <= charsToCache)
                            translationCache.TryAdd(split.Text, split.Translated);
                    }
                }));

                // Duplicates
                var duplicates = batch.SelectMany(line => line.Splits)
                    .GroupBy(split => split.Text)
                    .Where(group => group.Count() > 1);

                foreach (var splitDupes in duplicates)
                {
                    var firstSplit = splitDupes.First();

                    // Skip first one - it should be ok
                    foreach (var split in splitDupes.Skip(1))
                    {
                        if (split.Translated != firstSplit.Translated
                            || string.IsNullOrEmpty(split.Translated)
                            || forceRetranslation
                            || (config.TranslateFlagged && split.FlaggedForRetranslation))
                        {
                            split.Translated = firstSplit.Translated;
                            split.ResetFlags();
                            recordsProcessed++;
                            totalRecordsProcessed++;
                            bufferedRecords++;
                        }
                    }
                }

                logProcessed++;

                if (batchSize != 1 || (logProcessed % BatchlessLog == 0))
                    Console.WriteLine($"Line: {i + batchRange} of {totalLines} File: {textFileToTranslate.Path} Unprocessable: {incorrectLineCount} Processed: {totalRecordsProcessed}");

                if (bufferedRecords > BatchlessBuffer)
                {
                    Console.WriteLine($"Writing Buffer....");
                    File.WriteAllText(outputFile, serializer.Serialize(fileLines));
                    bufferedRecords = 0;
                }
            }

            var elapsed = stopWatch.ElapsedMilliseconds;
            var speed = recordsProcessed == 0 ? 0 : elapsed / recordsProcessed;
            Console.WriteLine($"Done: {totalLines} ({elapsed} ms ~ {speed}/line)");
            File.WriteAllText(outputFile, serializer.Serialize(fileLines));
        }
    }

    /// <summary>
    /// Per-file bookkeeping used by <see cref="TranslateViaLlmAsyncPooled"/> - one instance per
    /// entry in <paramref name="textFiles"/>, shared across every worker translating that file's
    /// splits so buffered-write flushing and progress counters stay correct under concurrency.
    /// </summary>
    private sealed class PooledFileState
    {
        public required TextFileToSplit TextFile { get; init; }
        public required string OutputFile { get; init; }
        public required List<TranslationLine> FileLines { get; init; }
        public required ISerializer Serializer { get; init; }
        public readonly object WriteLock = new();
        public readonly Stopwatch Stopwatch = Stopwatch.StartNew();
        public int RecordsProcessed;
        public int BufferedRecords;
    }

    /// <summary>
    /// Continuous worker-pool scheduler - the alternative to <see cref="TranslateViaLlmAsyncBatched"/>
    /// described in docs/OPTIMIZATION_PLAN.md item #1 (FanslationStudio.LlmKit repo). Instead of
    /// "one file at a time, one fixed-size batch at a time with a hard barrier", every unique split
    /// across every file in this run is flattened into a single work list and processed by a fixed
    /// number of concurrent workers (<see cref="LlmConfig.MaxConcurrency"/>) that each pull the next
    /// item as soon as they finish one - a slow item (retry/correction/sub-split) only holds up its
    /// own worker slot, not an entire batch or file boundary, and a file's last few slow items
    /// naturally overlap with the next file's first items since everything shares one pool.
    ///
    /// Per-file text dedup (GroupBy Text, translate the first occurrence, propagate to duplicates)
    /// is preserved exactly as in the batched scheduler and still scoped to one file at a time -
    /// only the *scheduling* is decoupled from batches/files, not the translation-cache-per-file
    /// prompt semantics (a given Chinese string can legitimately translate differently in two files
    /// with different glossary/prompt settings, so this intentionally does not dedup across files
    /// beyond what the existing run-wide <see cref="TranslationCacheMaxChars"/> cache already does).
    /// Duplicate propagation is done as a fast, LLM-call-free pass per file, run opportunistically
    /// before every buffered write for that file (so a cancelled/killed run only loses duplicates
    /// translated since the last flush) and once more after the whole pool drains.
    /// </summary>
    public static async Task TranslateViaLlmAsyncPooled(string workingDirectory, bool forceRetranslation,
        TextFileToSplit[] textFiles)
    {
        string inputPath = $"{workingDirectory}/Raw/Export";
        string outputPath = $"{workingDirectory}/Converted";

        if (!Directory.Exists(outputPath))
            Directory.CreateDirectory(outputPath);

        var (config, translationCache, client) = await PrepareTranslationRunAsync(workingDirectory, textFiles);
        using var _ = client;

        var maxConcurrency = config.MaxConcurrency ?? config.BatchSize ?? 20;

        // Load every file up-front (no translation yet) so work items from every file can be
        // flattened into one pool below.
        var fileStates = new List<PooledFileState>();
        foreach (var textFileToTranslate in textFiles)
        {
            var inputFile = $"{inputPath}/{textFileToTranslate.Path}";
            var outputFile = $"{outputPath}/{textFileToTranslate.Path}.yaml";

            if (!File.Exists(outputFile))
                File.Copy(inputFile, outputFile);

            var content = await File.ReadAllTextAsync(outputFile);

            var serializer = YamlHelper.CreateSerializer();
            var deserializer = YamlHelper.CreateDeserializer();
            var fileLines = deserializer.Deserialize<List<TranslationLine>>(content);

            fileStates.Add(new PooledFileState
            {
                TextFile = textFileToTranslate,
                OutputFile = outputFile,
                FileLines = fileLines,
                Serializer = serializer,
            });
        }

        int incorrectLineCount = 0;
        int totalRecordsProcessed = 0;

        // Diagnostic-only: captures why each split that ends up unprocessable (empty Translated
        // after retries are exhausted) failed validation, so a run can be inspected without
        // waiting for it to finish - flushed to disk periodically (see WriteUnprocessableItemsLog
        // below, called at the same cadence as the progress log) as well as once more at the end.
        var unprocessableItems = new ConcurrentBag<(string FilePath, string Raw, string Reason)>();
        var unprocessableLogPath = $"{workingDirectory}/TestResults/UnprocessableItems.log";

        // Unique-per-file splits (same dedup semantics as the batched scheduler), flattened across
        // every file into one global work list for the pool to consume.
        var workItems = fileStates
            .SelectMany(file => file.FileLines
                .SelectMany(line => line.Splits)
                .GroupBy(split => split.Text)
                .Select(group => group.First())
                .Select(split => (File: file, Split: split)))
            .ToList();

        // Same "does this item actually need work" condition used inside the loop below - computed
        // up front purely for a more useful denominator in the progress log. workItems.Count is
        // every unique split in the run (including ones already translated from a prior run, which
        // the loop below skips near-instantly) - logging progress against that count makes an
        // already-mostly-translated run look far more "stuck" than it really is.
        var pendingCount = workItems.Count(wi => string.IsNullOrEmpty(wi.Split.Translated)
            || forceRetranslation
            || (config.TranslateFlagged && wi.Split.FlaggedForRetranslation));

        Console.WriteLine($"Pooled translation: {workItems.Count} unique split(s) across {fileStates.Count} file(s) ({pendingCount} need translation), max concurrency {maxConcurrency}");

        // Progress-logging state: lets each "Processed: N" line report how long that interval of
        // BatchlessLog items took and how many retry/correction round-trips happened in it, instead
        // of just a running total - this is what shows whether a run is slow-to-start (cold cache,
        // connection warm-up, early unlucky retries) and then speeding up, or staying slow throughout.
        var runStopwatch = Stopwatch.StartNew();
        var progressLogLock = new object();
        long lastLogElapsedMs = 0;
        var lastLogRetryCount = Volatile.Read(ref _retryAttemptCounter);
        var lastLogEscalationCount = Volatile.Read(ref _escalationAttemptCounter);

        await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency }, async (item, _) =>
        {
            var (file, split) = item;

            if (string.IsNullOrEmpty(split.Text) || !split.SafeToTranslate)
                return;

            // File-restricted glossary/manual entries (only:/exclude:) must never be read from or
            // written into this shared cache - see IsFileRestrictedText.
            var isFileRestricted = IsFileRestrictedText(split.Text, config);

            var cacheHit = !isFileRestricted
                && translationCache.ContainsKey(split.Text)
                // We use this for name files etc which will be in cache
                && file.TextFile.EnableGlossary;

            if (string.IsNullOrEmpty(split.Translated)
                || forceRetranslation
                || (config.TranslateFlagged && split.FlaggedForRetranslation))
            {
                var original = split.Translated;

                if (cacheHit)
                    split.Translated = translationCache[split.Text];
                else if (TryGetOnlyFileDirectTranslation(split.Text, file.TextFile.Path, config, out var directResult))
                    // Exact "only" match for this file - use it directly, no LLM call.
                    split.Translated = directResult;
                else
                {
                    var result = await TranslateSplitAsync(config, split.Text, client, file.TextFile, column: split.Split);
                    split.Translated = result.Valid ? result.Result : string.Empty;

                    if (!result.Valid)
                    {
                        var reason = string.IsNullOrEmpty(result.CorrectionPrompt)
                            ? "(no correction prompt captured - likely an HttpRequestException/connection failure, see console for 'Request error' lines)"
                            : result.CorrectionPrompt;
                        var escalationNote = result.EscalationAttempted ? " [escalation attempted: yes]" : " [escalation attempted: no]";
                        unprocessableItems.Add((file.TextFile.Path, split.Text, reason + escalationNote));
                    }
                }

                split.ResetFlags(split.Translated != original);
                Interlocked.Increment(ref file.RecordsProcessed);
                var totalProcessed = Interlocked.Increment(ref totalRecordsProcessed);
                var buffered = Interlocked.Increment(ref file.BufferedRecords);

                if (totalProcessed % BatchlessLog == 0)
                {
                    lock (progressLogLock)
                    {
                        var elapsedNow = runStopwatch.ElapsedMilliseconds;
                        var intervalMs = elapsedNow - lastLogElapsedMs;
                        var currentRetryCount = Volatile.Read(ref _retryAttemptCounter);
                        var intervalRetries = currentRetryCount - lastLogRetryCount;
                        var currentEscalationCount = Volatile.Read(ref _escalationAttemptCounter);
                        var intervalEscalations = currentEscalationCount - lastLogEscalationCount;
                        var itemsPerSecond = intervalMs > 0 ? BatchlessLog * 1000.0 / intervalMs : 0;

                        Console.WriteLine($"Processed: {totalProcessed} of {pendingCount} pending ({workItems.Count} total) Unprocessable: {incorrectLineCount} | {BatchlessLog} took {intervalMs}ms (~{itemsPerSecond:F1}/s),\n   retries: {intervalRetries} (total: {currentRetryCount}), escalations: {intervalEscalations} (total: {currentEscalationCount}), elapsed: {elapsedNow}ms");

                        lastLogElapsedMs = elapsedNow;
                        lastLogRetryCount = currentRetryCount;
                        lastLogEscalationCount = currentEscalationCount;

                        WriteUnprocessableItemsLog(unprocessableLogPath, unprocessableItems);
                    }
                }

                if (buffered > BatchlessBuffer)
                {
                    lock (file.WriteLock)
                    {
                        // Re-check under the lock - another worker may have already flushed.
                        if (file.BufferedRecords > BatchlessBuffer)
                        {
                            Console.WriteLine($"Writing Buffer.... ({file.TextFile.Path})");
                            // Opportunistic duplicate propagation before every flush (not just at the
                            // end of the whole file) so a cancelled/killed run loses at most the
                            // in-flight duplicates since the last flush, not every duplicate in the
                            // file. Safe to call repeatedly - it only copies over translations that
                            // already exist on the first occurrence of each duplicate group.
                            PropagateDuplicates(file, forceRetranslation, config, ref totalRecordsProcessed);
                            File.WriteAllText(file.OutputFile, file.Serializer.Serialize(file.FileLines));
                            file.BufferedRecords = 0;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(split.Translated))
                Interlocked.Increment(ref incorrectLineCount);
            else if (!cacheHit && !isFileRestricted && split.Text.Length <= TranslationCacheMaxChars)
                //Two translations could be doing this at the same time
                translationCache.TryAdd(split.Text, split.Translated);
        });

        // Final pass per file: duplicates may still remain if a file's last flush happened before
        // its final unique split(s) finished translating, plus this writes every file at least
        // once even if it never crossed the buffered-write threshold.
        foreach (var file in fileStates)
        {
            PropagateDuplicates(file, forceRetranslation, config, ref totalRecordsProcessed);

            var elapsed = file.Stopwatch.ElapsedMilliseconds;
            var speed = file.RecordsProcessed == 0 ? 0 : elapsed / file.RecordsProcessed;
            Console.WriteLine($"Done: {file.FileLines.Count} ({elapsed} ms ~ {speed}/line) File: {file.TextFile.Path}");
            File.WriteAllText(file.OutputFile, file.Serializer.Serialize(file.FileLines));
        }

        Console.WriteLine($"Total Lines: {totalRecordsProcessed} records, Unprocessable: {incorrectLineCount}");

        // Final flush - covers any unprocessable items added since the last periodic write above.
        WriteUnprocessableItemsLog(unprocessableLogPath, unprocessableItems);
    }

    /// <summary>
    /// Overwrites the diagnostic unprocessable-items log with the current contents of
    /// <paramref name="unprocessableItems"/>. Called periodically during a run (not just once at
    /// the end) so the log can be inspected while a long run is still in progress. Overwrites
    /// rather than appends, so it always reflects this run's items so far, never a stale prior run.
    /// </summary>
    private static void WriteUnprocessableItemsLog(string logPath, ConcurrentBag<(string FilePath, string Raw, string Reason)> unprocessableItems)
    {
        if (unprocessableItems.IsEmpty)
            return;

        var logDir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            Directory.CreateDirectory(logDir);

        var logContent = string.Join("\n\n", unprocessableItems
            .OrderBy(x => x.FilePath)
            .ThenBy(x => x.Raw)
            .Select(x => $"[{x.FilePath}] RAW: {x.Raw}\n  REASON: {x.Reason}"));
        File.WriteAllText(logPath, logContent);
    }

    /// <summary>
    /// Propagates a translated split's result to every other split in the same file that shares the
    /// same source <see cref="TranslationSplit.Text"/> - a fast, LLM-call-free pass. Called both
    /// opportunistically before every buffered write (so a cancelled run only loses duplicates
    /// translated since the last flush, not every duplicate in the file) and once more at the end of
    /// <see cref="TranslateViaLlmAsyncPooled"/> to catch anything translated after the last flush.
    /// Safe to call repeatedly/concurrently for the same file since it is only ever invoked while
    /// holding <see cref="PooledFileState.WriteLock"/> for that file, and counters are updated
    /// atomically since other files' workers may be incrementing <paramref name="totalRecordsProcessed"/>
    /// at the same time.
    /// </summary>
    private static void PropagateDuplicates(PooledFileState file, bool forceRetranslation, LlmConfig config,
        ref int totalRecordsProcessed)
    {
        var duplicates = file.FileLines
            .SelectMany(line => line.Splits)
            .GroupBy(split => split.Text)
            .Where(group => group.Count() > 1);

        foreach (var splitDupes in duplicates)
        {
            var firstSplit = splitDupes.First();

            if (string.IsNullOrEmpty(firstSplit.Translated))
                continue;

            // Skip first one - it should be ok
            foreach (var split in splitDupes.Skip(1))
            {
                if (split.Translated != firstSplit.Translated
                    || string.IsNullOrEmpty(split.Translated)
                    || forceRetranslation
                    || (config.TranslateFlagged && split.FlaggedForRetranslation))
                {
                    split.Translated = firstSplit.Translated;
                    split.ResetFlags();
                    Interlocked.Increment(ref file.RecordsProcessed);
                    Interlocked.Increment(ref totalRecordsProcessed);
                }
            }
        }
    }

    /// <summary>
    /// Translates a set of independent pieces concurrently. Each piece goes through exactly one
    /// call to <see cref="TranslateSplitAsync"/> - no extra retry loop is layered on top here,
    /// because <see cref="TranslateSplitAsync"/> already retries a failing piece internally (up to
    /// <see cref="LlmConfig.RetryCount"/> whole-cell attempts, plus up to <see
    /// cref="LlmConfig.RetryCount"/> sentence-correction attempts for leftover-Chinese failures)
    /// before returning. Wrapping another <c>RetryCount</c>-bounded retry loop around a call to a
    /// function that already exhausts its own <c>RetryCount</c> budget internally squares the
    /// worst-case call count for a stubborn piece instead of adding to it - that was a real
    /// performance bug in an earlier version of this method.
    /// </summary>
    private static async Task<ValidationResult[]> TranslatePiecesWithRetryAsync(IReadOnlyList<string> pieces,
        LlmConfig config, HttpClient client, TextFileToSplit textFile, int? column = null)
    {
        return await Task.WhenAll(pieces.Select(piece => TranslateSplitAsync(config, piece, client, textFile, column: column)));
    }

    public static async Task<(bool split, string result)> SplitOnCharsIfNeededAsync(string splitCharacters, LlmConfig config, string raw, HttpClient client, TextFileToSplit textFile, int? column = null)
    {
        if (raw.Contains(splitCharacters))
        {
            var splits = raw.Split(splitCharacters);

            string suffix;

            if (splitCharacters == "-")
                suffix = " - ";
            else if (splitCharacters == ":")
                suffix = ": ";
            else
                suffix = splitCharacters;

            // Pieces are independent of each other - translate them concurrently, and retry only
            // the pieces that fail validation instead of discarding the whole cell the first time
            // any single piece fails. Order is preserved throughout.
            var translations = await TranslatePiecesWithRetryAsync(splits, config, client, textFile, column);

            // If any piece still fails after retries, we have to kill the lot
            if (translations.Any(t => !t.Valid) && !config.SkipLineValidation)
                return (true, string.Empty);

            var builder = new StringBuilder();
            foreach (var trans in translations)
                builder.Append($"{trans.Result}{suffix}");

            var result = builder.ToString();

            // Remove the very last suffix that was added
            if (splits.Length > 1)
                return (true, result[..^suffix.Length]);
            else
                return (true, result);
        }

        return (false, string.Empty);
    }

    public static async Task<(bool split, string result)> SplitBracketsRegexIfNeededAsync(LlmConfig config,
        string raw, HttpClient client,
        TextFileToSplit textFile,
        int? column = null)
    {
        // Collect all matches across all patterns and sort by position so multiple bracket types in
        // the same string are all handled in a single pass (e.g. "天竺国《无量寿经》【副本】4000钱")
        var allMatches = config.SplitRegexPatterns
            .SelectMany(pattern => Regex.Matches(raw, pattern).Cast<Match>())
            .OrderBy(m => m.Index)
            .ToList();

        if (allMatches.Count == 0)
            return (false, string.Empty);

        // Discard overlapping matches up-front so the remaining set can be translated concurrently
        // without affecting each other's outcome.
        var nonOverlappingMatches = new List<Match>();
        var lastOriginalIndexForFilter = 0;
        foreach (var match in allMatches)
        {
            if (match.Index < lastOriginalIndexForFilter)
                continue;

            nonOverlappingMatches.Add(match);
            lastOriginalIndexForFilter = match.Index + match.Length;
        }

        // Pre-translate each match's inner content separately (independent of each other, so done
        // concurrently, with failed pieces retried without discarding pieces that already
        // succeeded).
        var innerTranslations = await TranslatePiecesWithRetryAsync(
            nonOverlappingMatches.Select(match => match.Value[1..^1]).ToList(), config, client, textFile, column);

        if (innerTranslations.Any(t => !t.Valid) && !config.SkipLineValidation)
            return (true, string.Empty);

        // Build a `{n}`-placeholder template for the full sentence in a single forward pass
        // (appending literal text between matches), the same convention
        // CompoundFieldSplitter.Decompose/Reconstruct use elsewhere in the pipeline, instead of
        // hand-rolled in-place substring surgery with a running index/offset. This also means the
        // LLM sees a real `{n}` token (the template now Contains('{'), so
        // GenerateBaseMessages' DynamicPlaceholderPrompt kicks in) rather than a bare number it has
        // to infer is a placeholder from context alone.
        var templateBuilder = new StringBuilder();
        var lastEnd = 0;
        var bracketPairs = new List<(char Open, char Close)>();

        foreach (var match in nonOverlappingMatches)
        {
            templateBuilder.Append(raw, lastEnd, match.Index - lastEnd);
            templateBuilder.Append('{').Append(bracketPairs.Count).Append('}');
            bracketPairs.Add((match.Value[0], match.Value[^1]));
            lastEnd = match.Index + match.Length;
        }
        templateBuilder.Append(raw, lastEnd, raw.Length - lastEnd);

        var template = templateBuilder.ToString();

        // Translate the full sentence with `{n}` placeholders in place of the pre-translated bracket
        // contents to preserve surrounding context. No extra retry loop here - TranslateSplitAsync
        // already retries internally (up to RetryCount whole-cell attempts, plus up to RetryCount
        // sentence-correction attempts) before returning, so retrying its result again here would
        // square the worst-case call count instead of adding to it.
        var fullTrans = await TranslateSplitAsync(config, template, client, textFile, column: column);

        if (!fullTrans.Valid && !config.SkipLineValidation)
            return (true, string.Empty);

        // Restore the original bracket characters around each placeholder's translated content via
        // CompoundFieldSplitter.Reconstruct - the same safe `{n}` substitution used for CSV-cell
        // fragments, instead of hand-rolled string surgery. (Previously this step discarded
        // `innerTranslations` entirely and re-inserted a mangled substring of the placeholder
        // number itself - a real bug fixed by this refactor.)
        var bracketedFragments = innerTranslations
            .Select((trans, i) => $"{bracketPairs[i].Open}{trans.Result}{bracketPairs[i].Close}")
            .ToList();

        var result = CompoundFieldSplitter.Reconstruct(fullTrans.Result, bracketedFragments);

        return (true, result.Trim());
    }

    public static bool IsGameObjectReference(string raw)
    {
        // Check if it looks like a game object reference
        if (raw.Contains("/")
                && (raw.Contains("View")
                || raw.Contains("btn")
                || raw.Contains("Part")
                || raw.Contains("Text")))
            return true;
        return false;
    }


    public static async Task<ValidationResult> TranslateSplitAsync(LlmConfig config,
        string? raw,
        HttpClient client,
        TextFileToSplit textFile,
        string additionalPrompts = "",
        int? column = null)
    {
        if (string.IsNullOrEmpty(raw))
            return new ValidationResult(true, string.Empty); //Is ok because raw was empty

        var pattern = LineValidation.ChineseCharPattern;

        // If it is already translated or just special characters return it
        if (!Regex.IsMatch(raw, pattern))
            return new ValidationResult(true, raw);

        if (textFile.TextFileType == TextFileType.LocalTextString)
        {
            // Check if it looks like a game object reference
            if (IsGameObjectReference(raw))
                return new ValidationResult(true, raw);
        }

        // Prepare the raw by stripping out anything the LLM can't support
        var tokenReplacer = new StringTokenReplacer();
        var preparedRaw = LineValidation.PrepareRaw(raw, tokenReplacer);

        // If it is already translated or just special characters return it
        if (!Regex.IsMatch(preparedRaw, pattern))
            return new ValidationResult(true, LineValidation.CleanupLineBeforeSaving(preparedRaw, preparedRaw, textFile, tokenReplacer));

        var (regexSplit, regexResult) = await SplitBracketsRegexIfNeededAsync(config, raw, client, textFile, column);
        if (regexSplit)
            return new ValidationResult(LineValidation.CleanupLineBeforeSaving(regexResult, preparedRaw, textFile, tokenReplacer));

        // We do segementation here since saves context window by splitting // "。" doesnt work like u think it would        
        foreach (var splitCharacters in config.SplitCharactersList)
        {
            var (split, result) = await SplitOnCharsIfNeededAsync(splitCharacters, config, preparedRaw, client, textFile, column);

            // Because its recursive we want to bail out on the first successful one
            if (split)
                return new ValidationResult(LineValidation.CleanupLineBeforeSaving(result, preparedRaw, textFile, tokenReplacer));
        }

        if (ColorTagHelpers.StartsWithHalfColorTag(preparedRaw, out string start, out string end))
        {
            var startResult = await TranslateSplitAsync(config, start, client, textFile, column: column);
            var endResult = await TranslateSplitAsync(config, end, client, textFile, column: column);
            var combinedResult = $"{startResult.Result}{endResult.Result}";

            if (!config.SkipLineValidation && (!startResult.Valid || !endResult.Valid))
                return new ValidationResult(false, string.Empty);
            else
                return new ValidationResult(LineValidation.CleanupLineBeforeSaving($"{combinedResult}", preparedRaw, textFile, tokenReplacer));
        }

        if (preparedRaw.Length > 1 && LeadingStructuralPunctuation.Contains(preparedRaw[0]))
        {
            var leadingMark = preparedRaw[0];
            var remainder = preparedRaw[1..];
            var remainderResult = await TranslateSplitAsync(config, remainder, client, textFile, column: column);

            if (!config.SkipLineValidation && !remainderResult.Valid)
                return new ValidationResult(false, string.Empty);

            return new ValidationResult(LineValidation.CleanupLineBeforeSaving($"{leadingMark}{remainderResult.Result}", preparedRaw, textFile, tokenReplacer));
        }

        var cacheHit = !IsFileRestrictedText(preparedRaw, config)
            && config.Runtime.TranslationCache.ContainsKey(preparedRaw);
        if (cacheHit)
            return new ValidationResult(LineValidation.CleanupLineBeforeSaving(config.Runtime.TranslationCache[preparedRaw], preparedRaw, textFile, tokenReplacer));

        if (TryGetOnlyFileDirectTranslation(preparedRaw, textFile.Path, config, out var directResult))
            // Exact "only" match for this file - use it directly, no LLM call.
            return new ValidationResult(LineValidation.CleanupLineBeforeSaving(directResult, preparedRaw, textFile, tokenReplacer));

        // Calculate Executing model based on text
        var modelConfig = LlmHelpers.CalculateModelConfig(config, preparedRaw);

        try
        {
            var validationResult = await AttemptTranslationWithRetriesAsync(modelConfig, config.RetryCount ?? 1, isEscalation: false);

            // Escalation: only spend a second round of LLM calls against a different, presumably
            // stronger model if one is actually configured (see LlmConfig.EscalationModelName) and
            // it genuinely differs from the model already tried - resolving to the same model
            // would just repeat the exact same attempts we already know failed. No-op (identical
            // to pre-escalation behavior) whenever EscalationModelName is unset.
            if (!validationResult.Valid
                && !string.IsNullOrEmpty(config.EscalationModelName)
                && config.Runtime.Models.TryGetValue(config.EscalationModelName, out var escalationModelConfig)
                && escalationModelConfig.Model != modelConfig.Model)
            {
                validationResult = await AttemptTranslationWithRetriesAsync(escalationModelConfig, config.EscalationRetryCount ?? 1, isEscalation: true);
                validationResult.EscalationAttempted = true;
            }

            return validationResult;
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"Request error: {e.Message}");
            return new ValidationResult(string.Empty);
        }

        // Local function so both the primary attempt (against modelConfig, bounded by
        // config.RetryCount) and the optional escalation attempt (against a different model,
        // bounded by config.EscalationRetryCount) share identical retry/correction logic instead
        // of two copies drifting apart over time. Each call starts with a fresh message history
        // (its own GenerateBaseMessages call) and its own sentenceRetryCount budget - escalation
        // gets a clean slate against the new model rather than inheriting the failing model's
        // conversation history.
        async Task<ValidationResult> AttemptTranslationWithRetriesAsync(ModelExecutionConfig executingModel, int maxRetries, bool isEscalation)
        {
            var retryCount = 0;
            var preparedResult = string.Empty;
            var validationResult = new ValidationResult();
            var messages = GenerateBaseMessages(executingModel, config.Runtime.GlossaryLines, preparedRaw, textFile, additionalPrompts);

            // Shared, cumulative across every outer iteration (NOT reset per iteration) - see the
            // do-while loop below. Without this, a line that keeps re-triggering
            // RequiresSentenceBySentenceCorrection across multiple outer iterations could spend up
            // to (RetryCount outer iterations) * (RetryCount sentence-correction rounds each) LLM
            // round-trips instead of a bounded ~2x RetryCount total, which was the real cause of a
            // noticeable slowdown after this retry-scoping change was introduced.
            var sentenceRetryCount = 0;

            while (!validationResult.Valid && retryCount < maxRetries)
            {
                if (retryCount > 0)
                {
                    if (isEscalation)
                        Interlocked.Increment(ref _escalationAttemptCounter);
                    else
                        Interlocked.Increment(ref _retryAttemptCounter);
                }

                var llmResult = await TranslateMessagesAsync(client, config, executingModel, messages);
                preparedResult = LineValidation.PrepareResult(preparedRaw, llmResult, textFile, column);
                validationResult = LineValidation.CheckTransalationSuccessful(executingModel, preparedRaw, preparedResult, textFile, column);
                validationResult.Result = LineValidation.CleanupLineBeforeSaving(validationResult.Result, preparedRaw, textFile, tokenReplacer);

                if (config.SkipLineValidation)
                    validationResult.Valid = true;

                // Append history of failures
                if (!validationResult.Valid && config.CorrectionPromptsEnabled)
                {
                    // Use sentence-by-sentence correction for Chinese character issues. Keep
                    // re-invoking it (feeding the previous attempt's output back in) rather than
                    // immediately falling through to a full whole-cell retranslation - since
                    // CorrectSentenceBySentenceAsync only re-translates sentences that still
                    // contain Chinese characters, already-corrected sentences from a prior attempt
                    // are left untouched, so this only "retries" the sentence(s) still failing
                    // instead of discarding all the sentences that already succeeded.
                    if (validationResult.RequiresSentenceBySentenceCorrection)
                    {
                        var correctedResult = llmResult;

                        do
                        {
                            if (isEscalation)
                                Interlocked.Increment(ref _escalationAttemptCounter);
                            else
                                Interlocked.Increment(ref _retryAttemptCounter);

                            correctedResult = await CorrectSentenceBySentenceAsync(client, config, executingModel, preparedRaw, correctedResult, textFile);
                            preparedResult = LineValidation.PrepareResult(preparedRaw, correctedResult, textFile, column);
                            validationResult = LineValidation.CheckTransalationSuccessful(executingModel, preparedRaw, preparedResult, textFile, column);
                            validationResult.Result = LineValidation.CleanupLineBeforeSaving(validationResult.Result, preparedRaw, textFile, tokenReplacer);

                            if (config.SkipLineValidation)
                                validationResult.Valid = true;

                            sentenceRetryCount++;
                        }
                        while (!validationResult.Valid
                            && validationResult.RequiresSentenceBySentenceCorrection
                            && sentenceRetryCount < maxRetries);

                        // Still failing (and not simply another round of sentence correction, e.g.
                        // a different validation issue was introduced) after sentence-level retries
                        // are exhausted - fall back to a normal whole-cell correction attempt on the
                        // next outer retry iteration, same as before this change.
                        if (!validationResult.Valid)
                        {
                            messages = GenerateBaseMessages(executingModel, config.Runtime.GlossaryLines, preparedRaw, textFile);
                            var correctionPrompt = CalulateCorrectionPrompt(executingModel, validationResult, preparedRaw, correctedResult);
                            AddCorrectionMessages(messages, correctedResult, correctionPrompt);
                        }
                    }
                    else
                    {
                        var correctionPrompt = CalulateCorrectionPrompt(executingModel, validationResult, preparedRaw, llmResult);

                        // Regenerate base messages so we dont hit token limit by constantly appending retry history
                        messages = GenerateBaseMessages(executingModel, config.Runtime.GlossaryLines, preparedRaw, textFile);
                        AddCorrectionMessages(messages, llmResult, correctionPrompt);
                    }
                }

                retryCount++;
            }

            return validationResult;
        }
    }

    public static void AddCorrectionMessages(List<object> messages, string result, string correctionPrompt)
    {
        messages.Add(LlmHelpers.GenerateAssistantPrompt(result));
        messages.Add(LlmHelpers.GenerateUserPrompt(correctionPrompt));
    }

    public static async Task<string> CorrectSentenceBySentenceAsync(HttpClient client, LlmConfig config, ModelExecutionConfig executingModel, string raw, string failedResult, TextFileToSplit textFile)
    {
        // Split into sentences on '.'/'!'/'?' followed by whitespace or end-of-string, without
        // splitting inside a `{n}` placeholder token - a bare `. `/`! `/`? ` split can't handle
        // non-period sentence endings and would happily cut a placeholder like "{0}" in half if a
        // '.' ever appeared inside one.
        var sentences = SplitIntoSentences(failedResult);

        // Sentences are independent of each other - correct them concurrently instead of one at a
        // time. Task.WhenAll preserves input order in its result array.
        var correctedSentences = await Task.WhenAll(sentences.Select(async sentence =>
        {
            // Only correct sentences that contain Chinese characters
            if (Regex.IsMatch(sentence, LineValidation.ChineseCharPattern) && !Regex.IsMatch(sentence, LineValidation.ChinesePlaceholderPattern))
            {
                // For individual sentence correction, use a minimal prompt without the full original text
                // This prevents the LLM from re-translating everything
                var messages = new List<object>
                {
                    LlmHelpers.GenerateSystemPrompt(executingModel.Prompts["BaseSystemPrompt"]),
                    LlmHelpers.GenerateUserPrompt("The following sentence contains untranslated Chinese characters. Translate all Chinese characters to English while keeping the rest of the sentence intact."),
                    LlmHelpers.GenerateAssistantPrompt(sentence),
                    LlmHelpers.GenerateUserPrompt("Translate all Chinese characters in this sentence to English. " + executingModel.Prompts["BaseCorrectionSuffixPrompt"])
                };

                var correctedSentence = (await TranslateMessagesAsync(client, config, executingModel, messages)).Trim();

                // NOTE: deliberately no internal per-sentence retry loop here - the caller
                // (TranslateSplitAsync) already re-invokes this whole method again if any sentence
                // still fails, and since only sentences that still contain Chinese get re-sent, that
                // outer retry achieves the same "retry only the failed sentence" effect. Retrying
                // here too was a redundant multiplier (up to RetryCount extra calls per sentence,
                // per outer attempt) that made runs far slower without improving success rate.
                return correctedSentence;
            }

            // Sentence is fine, keep it as is
            return sentence;
        }));

        // Rejoin sentences with proper spacing
        return string.Join(" ", correctedSentences);
    }

    /// <summary>
    /// Splits text into sentences on '.'/'!'/'?' followed by whitespace or end-of-string, without
    /// splitting inside a `{n}` placeholder token (tracked via brace depth) or inside a
    /// double-quoted clause (single quotes are intentionally NOT tracked - they're overwhelmingly
    /// used as English contraction apostrophes in translated output, e.g. "don't"/"it's", and
    /// treating every apostrophe as a quote-toggle would misinterpret ordinary text far more often
    /// than it would correctly guard a real quoted clause). The trailing whitespace separator is
    /// consumed (not included in either sentence), matching the join-with-single-space behavior of
    /// the caller.
    /// </summary>
    private static List<string> SplitIntoSentences(string text)
    {
        var sentences = new List<string>();

        if (string.IsNullOrEmpty(text))
            return sentences;

        var current = new StringBuilder();
        var braceDepth = 0;
        var inDoubleQuote = false;

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            current.Append(c);

            if (c == '{')
                braceDepth++;
            else if (c == '}' && braceDepth > 0)
                braceDepth--;
            else if (c == '"')
                inDoubleQuote = !inDoubleQuote;

            var isSentenceEndChar = c is '.' or '!' or '?';
            if (isSentenceEndChar && braceDepth == 0 && !inDoubleQuote)
            {
                var isEndOfText = i == text.Length - 1;
                var nextIsWhitespace = !isEndOfText && char.IsWhiteSpace(text[i + 1]);

                if (isEndOfText || nextIsWhitespace)
                {
                    sentences.Add(current.ToString());
                    current.Clear();

                    if (nextIsWhitespace)
                        i++; // consume the separating whitespace character
                }
            }
        }

        if (current.Length > 0)
            sentences.Add(current.ToString());

        return sentences;
    }

    public static List<object> GenerateBaseMessages(ModelExecutionConfig config, List<GlossaryLine> glossaryLines, string raw, TextFileToSplit splitFile, string additionalSystemPrompt = "")
    {
        //Dynamically build prompt using whats in the raws
        var basePrompt = new StringBuilder();

        if (splitFile.EnableBasePrompts)
        {
            basePrompt.AppendLine(config.Prompts["BaseSystemPrompt"]);

            if (raw.Contains("<color"))
                basePrompt.AppendLine(config.Prompts["DynamicColorPrompt"]);
            else if (raw.Contains("</color>"))
                basePrompt.AppendLine(config.Prompts["DynamicCloseColorPrompt"]);

            // Qwen 2.5 hates size tags
            if (raw.Contains("<size"))
                basePrompt.AppendLine(config.Prompts["DynamicSizePrompt"]);
            else if (raw.Contains("</size>"))
                basePrompt.AppendLine(config.Prompts["DynamicCloseSizePrompt"]);

            //if (raw.Contains("·"))
            //    basePrompt.AppendLine(config.Prompts["DynamicSegement1Prompt"]);

            if (raw.Contains("<"))
            {
                var rawTags = HtmlTagHelpers.ExtractTagsListWithAttributes(raw, "color", "size");
                if (rawTags.Count > 0)
                {
                    var prompt = string.Format(config.Prompts["DynamicTagPrompt"], string.Join("\n", rawTags));
                    basePrompt.AppendLine(prompt);
                }
            }

            if (raw.Contains('{'))
                basePrompt.AppendLine(config.Prompts["DynamicPlaceholderPrompt"]);
        }

        if (!string.IsNullOrEmpty(splitFile.AdditionalPromptName))
            basePrompt.AppendLine(config.Prompts[splitFile.AdditionalPromptName]);
        basePrompt.AppendLine(additionalSystemPrompt);

        if (splitFile.EnableGlossary)
        {
            basePrompt.AppendLine("");
            basePrompt.AppendLine(config.Prompts["BaseGlossaryPrompt"]);
            basePrompt.AppendLine(GlossaryLine.AppendPromptsFor(raw, glossaryLines, splitFile.Path));
        }

        if (splitFile.EnableBasePrompts)
        {
            basePrompt.AppendLine("");
            basePrompt.AppendLine(config.Prompts["BaseSystemSuffixPrompt"]);
        }

        return
        [
            LlmHelpers.GenerateSystemPrompt(basePrompt.ToString()),
            LlmHelpers.GenerateUserPrompt(raw)
        ];
    }

    public static string CalulateCorrectionPrompt(ModelExecutionConfig modelConfig, ValidationResult validationResult, string raw, string result)
    {
        // Return the concatenated specific correction prompts with the shared suffix
        // Context is provided by conversation structure (User: original, Assistant: failed attempt, User: corrections)
        if (string.IsNullOrEmpty(validationResult.CorrectionPrompt))
            return string.Empty;

        return validationResult.CorrectionPrompt + modelConfig.Prompts["BaseCorrectionSuffixPrompt"];
    }

    public static void AddPromptWithValues(this StringBuilder builder, ModelExecutionConfig config, string promptName, params string[] values)
    {
        var prompt = string.Format(config.Prompts[promptName], values);
        builder.Append(' ');
        builder.Append(prompt);
    }

    public static async Task<string> TranslateMessagesAsync(HttpClient client, LlmConfig config, ModelExecutionConfig modelToUse, List<object> messages)
    {
        // Generate based on what would have been created
        var requestData = LlmHelpers.GenerateLlmRequestData(modelToUse, messages);

        // Send correction & Get result
        HttpContent content = new StringContent(requestData, Encoding.UTF8, "application/json");

        try
        {
            // Set Bearer token if required and not already set
            var requiresApiKey = modelToUse.ApiKeyRequired ?? false;

            if (requiresApiKey)
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", modelToUse.ApiKey);
            else
                client.DefaultRequestHeaders.Authorization = null;

            HttpResponseMessage response = await client.PostAsync(modelToUse.Url, content);
            string responseBody = await response.Content.ReadAsStringAsync();

            if ((int)response.StatusCode == 429)
            {
                // Too Many Requests - simple exponential backoff
                int retryDelay = 5000; // start with 2 seconds
                int maxDelay = 60000; // max 30 seconds
                int retries = 0;
                var backoffStopwatch = Stopwatch.StartNew();
                while ((int)response.StatusCode == 429 && retries < 5)
                {
                    Console.WriteLine($"Received 429 Too Many Requests. Backing off attempt {retries + 1}/5, waiting {retryDelay}ms...");
                    await Task.Delay(retryDelay);
                    retryDelay = Math.Min(retryDelay * 2, maxDelay);
                    response = await client.PostAsync(modelToUse.Url, content);
                    responseBody = await response.Content.ReadAsStringAsync();
                    retries++;
                }

                if (retries > 0)
                    Console.WriteLine($"429 backoff finished after {retries} attempt(s), {backoffStopwatch.ElapsedMilliseconds}ms blocked, final status {(int)response.StatusCode}.");
            }

            response.EnsureSuccessStatusCode();

            using var jsonDoc = JsonDocument.Parse(responseBody);

            var result = string.Empty;

            if (responseBody.Contains("\"choices\":"))
            {
                result = jsonDoc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString()
                    ?.Trim() ?? string.Empty;
            }
            else
            {
                result = jsonDoc.RootElement
                    .GetProperty("message")!
                    .GetProperty("content")!
                    .GetString()
                    ?.Trim() ?? string.Empty;
            }

            // Remove any <think> tags and their content
            result = RemoveThinkTags(result);

            return result;
        }
        catch (Exception e)
        {
            if (config.SkipLineValidation)
            {
                Console.WriteLine($"Exception on: {requestData}");
                Console.WriteLine($"Exception message: {e.Message}");
                return "";
            }
            else
                throw;
        }
    }

    private static string RemoveThinkTags(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Regex to remove <think>...</think> tags and their content, including multiline
        return Regex.Replace(input, @"<think>.*?</think>\n\n", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
    }
}
