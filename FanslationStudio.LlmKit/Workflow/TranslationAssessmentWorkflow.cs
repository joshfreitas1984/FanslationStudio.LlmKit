using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Workflow;

public static class TranslationAssessmentWorkflow
{
    public static async Task RunAsync(string workingDirectory, TextFileToSplit[] textFiles, GameHooks? hooks = null)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory, hooks);
        var settings = config.TranslationAssessment;

        if (!settings.Enabled)
        {
            Console.WriteLine("Translation assessment is disabled.");
            return;
        }

        if (settings.ModelNames.Count == 0)
            throw new InvalidOperationException("TranslationAssessment.ModelNames must contain at least one configured model.");

        var (samples, totalCorpusCharacters) = LoadSamples(workingDirectory, textFiles, settings.SampleSize,
            settings.FullCellSampleRatio, settings.SampleSeed, settings.PinnedSampleSources);
        if (samples.Count == 0)
            throw new InvalidOperationException("The translation assessment found no translatable samples under Raw/Export.");

        var outputDirectory = Path.Combine(workingDirectory, settings.OutputPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(outputDirectory);

        var modelReports = new List<AssessmentModelResult>();
        var configuredModels = new Dictionary<string, ModelExecutionConfig>(config.Runtime.Models);
        for (var modelIndex = 0; modelIndex < settings.ModelNames.Count; modelIndex++)
        {
            var modelName = settings.ModelNames[modelIndex];
            if (!configuredModels.TryGetValue(modelName, out var model))
                throw new InvalidOperationException($"Translation assessment model '{modelName}' is not configured.");

            Console.WriteLine($"Assessment model {modelIndex + 1}/{settings.ModelNames.Count}: {modelName} ({samples.Count} samples)");
            var report = await RunModelAsync(workingDirectory, outputDirectory, config, modelName, model,
                samples, totalCorpusCharacters, textFiles, hooks);
            modelReports.Add(report);
            Console.WriteLine($"Assessment model complete: {modelName} | {report.Completed} completed, {report.Failed} failed | {report.ElapsedMilliseconds}ms");
        }

        var comparison = new TranslationAssessmentComparison
        {
            SampleSize = samples.Count,
            SampleSeed = settings.SampleSeed,
            SampleFingerprint = Fingerprint(samples),
            Models = modelReports,
            GeneratedAtUtc = DateTime.UtcNow,
        };
        WriteYamlAtomically(Path.Combine(outputDirectory, "Comparison.yaml"), comparison);
    }

    private static async Task<AssessmentModelResult> RunModelAsync(
        string workingDirectory,
        string outputDirectory,
        LlmConfig config,
        string modelName,
        ModelExecutionConfig model,
        IReadOnlyList<AssessmentSample> samples,
        long totalCorpusCharacters,
        TextFileToSplit[] textFiles,
        GameHooks? hooks)
    {
        var modelDirectory = Path.Combine(outputDirectory, SafeName(modelName));
        Directory.CreateDirectory(modelDirectory);
        var resultPath = Path.Combine(modelDirectory, "Results.yaml");
        var fingerprint = Fingerprint(samples);
        var report = LoadOrCreateReport(resultPath, modelName, samples, fingerprint, config.TranslationAssessment.SampleSeed);

        if (report.Status == "completed")
            return report.ToSummary();

        var singleModelConfig = new Dictionary<string, ModelExecutionConfig>
        {
            [modelName] = model,
        };
        config.Runtime.Models = singleModelConfig;
        config.Runtime.TranslationCache.Clear();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var textFileByPath = textFiles.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var runStopwatch = Stopwatch.StartNew();
        var completedBeforeRun = report.Results.Count(x => x.Status == "completed");
        if (completedBeforeRun > 0)
            Console.WriteLine($"  Resuming {modelName}: {completedBeforeRun}/{samples.Count} samples already complete");

        for (var sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            var sample = samples[sampleIndex];
            if (report.Results.Any(x => x.SampleId == sample.SampleId && x.Status == "completed"))
                continue;

            if (!textFileByPath.TryGetValue(sample.FilePath, out var textFile))
                throw new InvalidOperationException($"Assessment sample references unknown file '{sample.FilePath}'.");

            Console.WriteLine($"  [{sampleIndex + 1}/{samples.Count}] {modelName} | {sample.FilePath} | {sample.Source[..Math.Min(60, sample.Source.Length)]}");
            var stopwatch = Stopwatch.StartNew();
            ValidationResult result;
            try
            {
                result = await TranslationService.TranslateSplitAsync(config, sample.Source, client, textFile,
                    column: sample.Column);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                result = new ValidationResult(false, string.Empty)
                {
                    CorrectionPrompt = exception.Message,
                };
            }

            stopwatch.Stop();
            report.Results.RemoveAll(x => x.SampleId == sample.SampleId);
            report.Results.Add(new AssessmentSampleResult
            {
                SampleId = sample.SampleId,
                SampleKind = sample.SampleKind,
                FilePath = sample.FilePath,
                Source = sample.Source,
                Translation = result.Result,
                Status = result.Valid ? "completed" : "failed",
                StructuralPass = result.Valid && !Regex.IsMatch(result.Result, LineValidation.ChineseCharPattern),
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                Error = result.Valid ? string.Empty : result.CorrectionPrompt,
            });
            WriteYamlAtomically(resultPath, report);
            Console.WriteLine($"    {(result.Valid ? "completed" : "failed")} in {stopwatch.ElapsedMilliseconds}ms");
        }

        runStopwatch.Stop();
        report.Status = "completed";
        report.CompletedAtUtc = DateTime.UtcNow;
        report.ElapsedMilliseconds = runStopwatch.ElapsedMilliseconds;
        report.TotalCorpusCharacters = totalCorpusCharacters;
        report.EstimatedFullCorpusMilliseconds = report.ElapsedMilliseconds <= 0 || report.Results.Count == 0
            ? 0
            : (long)Math.Ceiling((double)totalCorpusCharacters / Math.Max(1, report.Results.Sum(x => x.Source.Length)) * report.ElapsedMilliseconds);
        report.CharactersPerSecond = report.ElapsedMilliseconds <= 0
            ? 0
            : report.Results.Sum(x => (long)x.Source.Length) / (report.ElapsedMilliseconds / 1000.0);
        report.AverageMilliseconds = report.Results.Count == 0
            ? 0
            : report.Results.Average(x => x.ElapsedMilliseconds);
        report.P95Milliseconds = Percentile(report.Results.Select(x => x.ElapsedMilliseconds), 0.95);
        WriteYamlAtomically(resultPath, report);
        return report.ToSummary();
    }

    private static AssessmentModelResultFile LoadOrCreateReport(string path, string modelName,
        IReadOnlyList<AssessmentSample> samples, string fingerprint, int seed)
    {
        if (!File.Exists(path))
            return new AssessmentModelResultFile
            {
                ModelName = modelName,
                SampleSize = samples.Count,
                SampleSeed = seed,
                SampleFingerprint = fingerprint,
            };

        var report = YamlHelper.CreateDeserializer().Deserialize<AssessmentModelResultFile>(File.ReadAllText(path));
        if (report == null || report.ModelName != modelName || report.SampleSeed != seed || report.SampleFingerprint != fingerprint)
            throw new InvalidOperationException($"Assessment results at '{path}' do not match the current model or sample. Move the old Results.yaml before starting a new assessment.");

        report.Results ??= [];
        return report;
    }

    private static (List<AssessmentSample> Samples, long TotalCorpusCharacters) LoadSamples(string workingDirectory, TextFileToSplit[] textFiles,
        int sampleSize, double fullCellSampleRatio, int seed, IReadOnlyList<string> pinnedSampleSources)
    {
        var deserializer = YamlHelper.CreateDeserializer();
        var candidates = new List<AssessmentSample>();
        long totalCorpusCharacters = 0;
        foreach (var textFile in textFiles)
        {
            var path = Path.Combine(workingDirectory, "Raw", "Export", textFile.Path + ".yaml");
            if (!File.Exists(path))
                continue;

            var lines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(path)) ?? [];
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                AddFullCellSamples(candidates, textFile, line, lineIndex);
                for (var splitIndex = 0; splitIndex < line.Splits.Count; splitIndex++)
                {
                    var split = line.Splits[splitIndex];
                    if (!split.SafeToTranslate || string.IsNullOrWhiteSpace(split.Text)
                        || !Regex.IsMatch(split.Text, LineValidation.ChineseCharPattern))
                        continue;

                    totalCorpusCharacters += split.Text.Length;

                    var identity = $"{textFile.Path}|{line.RawIndex}|{lineIndex}|{split.SplitPath}|{split.Split}|{split.SubIndex}|{split.Text}";
                    candidates.Add(new AssessmentSample
                    {
                        SampleId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..16],
                        FilePath = textFile.Path,
                        Column = split.Split,
                        Source = split.Text,
                    });
                }
            }
        }

        var random = new Random(seed);
        var pinned = ResolvePinnedSamples(candidates, pinnedSampleSources);
        var pinnedIds = pinned.Select(x => x.SampleId).ToHashSet();

        var fullCellSamples = candidates.Where(x => x.SampleKind == "fullCell" && !pinnedIds.Contains(x.SampleId)).OrderBy(_ => random.Next()).ToList();
        var splitSamples = candidates.Where(x => x.SampleKind == "split" && !pinnedIds.Contains(x.SampleId)).OrderBy(_ => random.Next()).ToList();
        var fullCellCount = Math.Clamp((int)Math.Round(sampleSize * fullCellSampleRatio), 0, sampleSize);
        var selected = fullCellSamples.Take(fullCellCount)
            .Concat(splitSamples.Take(Math.Max(0, sampleSize - fullCellCount)))
            .ToList();

        if (selected.Count < sampleSize)
        {
            var selectedIds = selected.Select(x => x.SampleId).ToHashSet();
            selected.AddRange(candidates
                .Where(x => !selectedIds.Contains(x.SampleId) && !pinnedIds.Contains(x.SampleId))
                .OrderBy(_ => random.Next())
                .Take(sampleSize - selected.Count));
        }

        selected.InsertRange(0, pinned);
        return (selected, totalCorpusCharacters);
    }

    private static List<AssessmentSample> ResolvePinnedSamples(List<AssessmentSample> candidates, IReadOnlyList<string> pinnedSampleSources)
    {
        var pinned = new List<AssessmentSample>();
        var seen = new HashSet<string>();
        foreach (var pinnedSource in pinnedSampleSources)
        {
            if (!seen.Add(pinnedSource))
                continue;

            var match = candidates.FirstOrDefault(x => x.Source == pinnedSource);
            if (match == null)
            {
                Console.WriteLine($"  Warning: pinned assessment sample not found in corpus (skipped): {pinnedSource[..Math.Min(40, pinnedSource.Length)]}...");
                continue;
            }

            pinned.Add(match);
        }

        return pinned;
    }

    private static void AddFullCellSamples(List<AssessmentSample> candidates, TextFileToSplit textFile,
        TranslationLine line, int lineIndex)
    {
        foreach (var template in line.Templates)
        {
            var fragments = line.Splits
                .Where(split => split.Split == template.Split && split.SplitPath == template.SplitPath)
                .OrderBy(split => split.SubIndex)
                .ToList();
            if (fragments.Count == 0)
                continue;

            var source = CompoundFieldSplitter.Reconstruct(template.Template, fragments.Select(x => x.Text).ToList());
            if (string.IsNullOrWhiteSpace(source) || !Regex.IsMatch(source, LineValidation.ChineseCharPattern))
                continue;

            var identity = $"{textFile.Path}|{line.RawIndex}|{lineIndex}|fullCell|{template.SplitPath}|{template.Split}|{source}";
            candidates.Add(new AssessmentSample
            {
                SampleId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..16],
                SampleKind = "fullCell",
                FilePath = textFile.Path,
                Column = template.Split,
                Source = source,
            });
        }
    }

    private static string Fingerprint(IEnumerable<AssessmentSample> samples) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', samples.Select(x => x.SampleId))))).ToLowerInvariant();

    private static string SafeName(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static long Percentile(IEnumerable<long> values, double percentile)
    {
        var ordered = values.OrderBy(x => x).ToList();
        if (ordered.Count == 0)
            return 0;

        var index = (int)Math.Ceiling(ordered.Count * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }

    private static void WriteYamlAtomically(string path, object value)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, YamlHelper.CreateSerializer().Serialize(value), Encoding.UTF8);
        File.Move(temporaryPath, path, true);
    }

    private sealed class AssessmentSample
    {
        public string SampleId { get; set; } = string.Empty;
        public string SampleKind { get; set; } = "split";
        public string FilePath { get; set; } = string.Empty;
        public int Column { get; set; }
        public string Source { get; set; } = string.Empty;
    }

    public sealed class AssessmentModelResultFile
    {
        public string ModelName { get; set; } = string.Empty;
        public string Status { get; set; } = "running";
        public int SampleSize { get; set; }
        public int SampleSeed { get; set; }
        public string SampleFingerprint { get; set; } = string.Empty;
        public DateTime? CompletedAtUtc { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long TotalCorpusCharacters { get; set; }
        public long EstimatedFullCorpusMilliseconds { get; set; }
        public double CharactersPerSecond { get; set; }
        public double AverageMilliseconds { get; set; }
        public long P95Milliseconds { get; set; }
        public List<AssessmentSampleResult> Results { get; set; } = [];

        public AssessmentModelResult ToSummary() => new()
        {
            ModelName = ModelName,
            Status = Status,
            Completed = Results.Count(x => x.Status == "completed"),
            Failed = Results.Count(x => x.Status == "failed"),
            StructuralPassRate = Results.Count == 0 ? 0 : Results.Count(x => x.StructuralPass) / (double)Results.Count,
            ElapsedMilliseconds = ElapsedMilliseconds,
            EstimatedFullCorpusMilliseconds = EstimatedFullCorpusMilliseconds,
            CharactersPerSecond = CharactersPerSecond,
            AverageMilliseconds = AverageMilliseconds,
            P95Milliseconds = P95Milliseconds,
            HumanAccuracyScore = Results.Where(x => x.HumanAccuracyScore.HasValue).Select(x => x.HumanAccuracyScore!.Value).DefaultIfEmpty().Average(),
        };
    }

    public sealed class AssessmentSampleResult
    {
        public string SampleId { get; set; } = string.Empty;
        public string SampleKind { get; set; } = "split";
        public string FilePath { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool StructuralPass { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public string Error { get; set; } = string.Empty;
        public int? HumanAccuracyScore { get; set; }
        public string HumanReviewNotes { get; set; } = string.Empty;
    }

    private sealed class TranslationAssessmentComparison
    {
        public int SampleSize { get; set; }
        public int SampleSeed { get; set; }
        public string SampleFingerprint { get; set; } = string.Empty;
        public DateTime GeneratedAtUtc { get; set; }
        public List<AssessmentModelResult> Models { get; set; } = [];
    }

    public sealed class AssessmentModelResult
    {
        public string ModelName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int Completed { get; set; }
        public int Failed { get; set; }
        public double StructuralPassRate { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long EstimatedFullCorpusMilliseconds { get; set; }
        public double CharactersPerSecond { get; set; }
        public double AverageMilliseconds { get; set; }
        public long P95Milliseconds { get; set; }
        public double HumanAccuracyScore { get; set; }
    }
}