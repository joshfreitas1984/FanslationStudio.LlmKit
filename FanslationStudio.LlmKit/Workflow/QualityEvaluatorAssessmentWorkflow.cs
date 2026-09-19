using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Report-only comparison of QC evaluator models against a human-labelled gold set. Unlike the
/// translation assessment, this workflow never writes Converted data and evaluates the same
/// existing translation with every model.
/// </summary>
public static class QualityEvaluatorAssessmentWorkflow
{
    public static async Task RunAsync(string workingDirectory, GameHooks? hooks = null)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory, hooks);
        var settings = config.QualityEvaluatorAssessment;
        if (!settings.Enabled)
        {
            Console.WriteLine("Quality evaluator assessment is disabled.");
            return;
        }

        var modelNames = settings.ModelNames;
        if (modelNames.Count == 0)
            throw new InvalidOperationException("QualityEvaluatorAssessment.ModelNames must contain at least one configured model.");

        var goldSetFile = Path.Combine(workingDirectory, settings.GoldSetPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(goldSetFile))
            throw new FileNotFoundException("QC evaluator gold set was not found.", goldSetFile);

        var goldSet = YamlHelper.CreateDeserializer().Deserialize<GoldSet>(File.ReadAllText(goldSetFile))
            ?? throw new InvalidOperationException($"QC evaluator gold set '{goldSetFile}' was empty.");
        ValidateGoldSet(goldSet);

        var outputDirectory = Path.Combine(workingDirectory, settings.OutputPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(outputDirectory);
        var fingerprint = Fingerprint(goldSet);
        var configuredModels = new Dictionary<string, ModelExecutionConfig>(config.Runtime.Models);
        var reports = new List<EvaluatorSummary>();

        foreach (var modelName in modelNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!configuredModels.TryGetValue(modelName, out var model))
                throw new InvalidOperationException($"QC evaluator model '{modelName}' is not configured.");
            if (!model.Prompts.ContainsKey("BaseQualityReviewPrompt"))
                throw new InvalidOperationException($"QC evaluator model '{modelName}' has no BaseQualityReviewPrompt.");

            Console.WriteLine($"QC evaluator assessment: {modelName}");
            var report = await RunModelAsync(config, modelName, model, goldSet, fingerprint, outputDirectory);
            reports.Add(report.ToSummary());
        }

        WriteYamlAtomically(Path.Combine(outputDirectory, "Comparison.yaml"), new EvaluatorComparison
        {
            SchemaVersion = 1,
            GoldSetFingerprint = fingerprint,
            EvaluatorCount = reports.Count,
            GeneratedAtUtc = DateTime.UtcNow,
            Evaluators = reports,
        });
    }

    private static async Task<EvaluatorResultFile> RunModelAsync(
        LlmConfig config,
        string modelName,
        ModelExecutionConfig model,
        GoldSet goldSet,
        string fingerprint,
        string outputDirectory)
    {
        var modelDirectory = Path.Combine(outputDirectory, SafeName(modelName));
        Directory.CreateDirectory(modelDirectory);
        var resultPath = Path.Combine(modelDirectory, "Results.yaml");
        var report = LoadOrCreateReport(resultPath, modelName, goldSet, fingerprint);
        if (report.Status == "completed")
            return report;

        config.Runtime.Models = new Dictionary<string, ModelExecutionConfig> { [modelName] = model };
        config.Runtime.TranslationCache.Clear();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var stopwatch = Stopwatch.StartNew();

        foreach (var item in goldSet.Items)
        {
            foreach (var candidate in item.Candidates)
            {
                if (!item.Labels.TryGetValue(candidate.Key, out var expected))
                    continue;

                var resultId = $"detection:{item.SampleId}:{candidate.Key}:{modelName}";
                if (report.Results.Any(x => x.ResultId == resultId))
                    continue;

                report.Results.Add(await ReviewDetectionAsync(config, model, client, resultId,
                    item.SampleId, item.SampleKind, item.Source, candidate.Value,
                    expected));
                WriteYamlAtomically(resultPath, report);
            }
        }

        foreach (var item in goldSet.CorrectionSamples)
        {
            var resultId = $"correction:{item.SampleId}:{modelName}";
            if (report.Results.Any(x => x.ResultId == resultId))
                continue;

            report.Results.Add(await ReviewCorrectionAsync(config, model, client, resultId, item, modelName));
            WriteYamlAtomically(resultPath, report);
        }

        stopwatch.Stop();
        report.Status = "completed";
        report.CompletedAtUtc = DateTime.UtcNow;
        report.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        WriteYamlAtomically(resultPath, report);
        return report;
    }

    private static async Task<EvaluatorResult> ReviewDetectionAsync(
        LlmConfig config,
        ModelExecutionConfig model,
        HttpClient client,
        string resultId,
        string sampleId,
        string sampleKind,
        string source,
        string translation,
        GoldLabel expected)
    {
        var stopwatch = Stopwatch.StartNew();
        var tokenReplacer = new StringTokenReplacer();
        var verdict = await QualityReviewWorkflow.GetLlmVerdictAsync(config, model, client, source,
            tokenReplacer.Replace(source), tokenReplacer.Replace(translation), string.Empty);
        stopwatch.Stop();

        var actual = verdict.Success && verdict.Defect == QcDefectCategory.None
            ? "Pass"
            : verdict.Success && verdict.Defect == QcDefectCategory.Uncertain
                ? "Abstain"
                : "Defect";
        return new EvaluatorResult
        {
            ResultId = resultId,
            SampleId = sampleId,
            SampleKind = sampleKind,
            EvaluationKind = "detection",
            Source = source,
            CurrentTranslation = translation,
            ExpectedLabel = expected.Label,
            ActualLabel = actual,
            ExpectedDefectCategories = expected.DefectCategories,
            ActualDefectCategory = verdict.Defect.ToString(),
            ParseSuccess = verdict.Success,
            Score = verdict.Score,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        };
    }

    private static async Task<EvaluatorResult> ReviewCorrectionAsync(
        LlmConfig config,
        ModelExecutionConfig model,
        HttpClient client,
        string resultId,
        CorrectionSample item,
        string modelName)
    {
        var stopwatch = Stopwatch.StartNew();
        var tokenReplacer = new StringTokenReplacer();
        var defect = ParseCategory(item.DefectCategories.FirstOrDefault());
        var verdict = model.Prompts.ContainsKey("BaseQualityReviewVerificationPrompt")
            ? await QualityReviewWorkflow.GetVerificationVerdictAsync(config, model, client, item.Source,
                tokenReplacer.Replace(item.Source), tokenReplacer.Replace(item.CurrentTranslation), string.Empty,
                defect, tokenReplacer.Replace(item.ProposedCorrection))
            : new QualityReviewWorkflow.ScoreVerdict(false, QcDefectCategory.Unknown, 0);
        stopwatch.Stop();

        var actualSafety = !verdict.Success
            ? "Unscored"
            : verdict.Defect == QcDefectCategory.None
                ? "Unnecessary"
                : verdict.Score >= config.QualityReview.MinAcceptableScore ? "Safe" : "Harmful";
        return new EvaluatorResult
        {
            ResultId = resultId,
            SampleId = item.SampleId,
            SampleKind = item.SampleKind,
            EvaluationKind = "correction",
            Source = item.Source,
            CurrentTranslation = item.CurrentTranslation,
            ProposedCorrection = item.ProposedCorrection,
            ExpectedLabel = item.Label,
            ActualLabel = verdict.Defect == QcDefectCategory.None ? "Pass" : "Defect",
            ExpectedDefectCategories = item.DefectCategories,
            ActualDefectCategory = verdict.Defect.ToString(),
            ExpectedCorrectionSafety = item.CorrectionSafety,
            ActualCorrectionSafety = actualSafety,
            ParseSuccess = verdict.Success,
            Score = verdict.Score,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        };
    }

    private static QcDefectCategory ParseCategory(string? category) => category?.ToLowerInvariant() switch
    {
        "dropped-content" => QcDefectCategory.DroppedContent,
        "domain-term" or "terminology" => QcDefectCategory.DomainTerm,
        "lost-idiom" => QcDefectCategory.LostIdiom,
        "untranslated-pinyin" => QcDefectCategory.UntranslatedPinyin,
        "garbled-number" => QcDefectCategory.GarbledNumber,
        "hard-to-parse-seam" or "omitted-separator" => QcDefectCategory.HardToParseSeam,
        _ => QcDefectCategory.OtherNamedDefect,
    };

    private static EvaluatorResultFile LoadOrCreateReport(string path, string modelName, GoldSet goldSet, string fingerprint)
    {
        if (!File.Exists(path))
            return new EvaluatorResultFile { ModelName = modelName, GoldSetFingerprint = fingerprint };
        var report = YamlHelper.CreateDeserializer().Deserialize<EvaluatorResultFile>(File.ReadAllText(path));
        if (report == null || report.ModelName != modelName || report.GoldSetFingerprint != fingerprint)
            throw new InvalidOperationException($"QC evaluator results at '{path}' do not match the current model or gold set.");
        report.Results ??= [];
        return report;
    }

    private static void ValidateGoldSet(GoldSet goldSet)
    {
        if (goldSet.Items.Count == 0 && goldSet.CorrectionSamples.Count == 0)
            throw new InvalidOperationException("The QC evaluator gold set contains no samples.");
        var ids = new HashSet<string>();
        foreach (var id in goldSet.Items.Select(x => x.SampleId).Concat(goldSet.CorrectionSamples.Select(x => x.SampleId)))
            if (!ids.Add(id))
                throw new InvalidOperationException($"Duplicate QC evaluator gold-set sample ID '{id}'.");
    }

    internal static GoldSet LoadGoldSet(string yaml)
    {
        var goldSet = YamlHelper.CreateDeserializer().Deserialize<GoldSet>(yaml)
            ?? throw new InvalidOperationException("The QC evaluator gold set was empty.");
        ValidateGoldSet(goldSet);
        return goldSet;
    }

    internal static string CalculateFingerprint(GoldSet goldSet) => Fingerprint(goldSet);

    private static string Fingerprint(GoldSet goldSet) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', goldSet.Items.SelectMany(x => x.Candidates.Select(candidate =>
                string.Join('|', x.SampleId, candidate.Key, candidate.Value,
                    x.Labels.TryGetValue(candidate.Key, out var label) ? label.Label : "",
                    x.Labels.TryGetValue(candidate.Key, out label) ? string.Join(',', label.DefectCategories) : "")))
                .Concat(goldSet.CorrectionSamples.Select(x => string.Join('|', x.SampleId, x.Source,
                    x.CurrentTranslation, x.ProposedCorrection, x.Label, string.Join(',', x.DefectCategories)))))))).ToLowerInvariant();

    private static string SafeName(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static void WriteYamlAtomically(string path, object value)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, YamlHelper.CreateSerializer().Serialize(value), Encoding.UTF8);
        File.Move(temporaryPath, path, true);
    }

    public sealed class GoldSet
    {
        public int SchemaVersion { get; set; }
        public string LabelVersion { get; set; } = string.Empty;
        public List<GoldItem> Items { get; set; } = [];
        public List<CorrectionSample> CorrectionSamples { get; set; } = [];
    }

    public sealed class GoldItem
    {
        public string SampleId { get; set; } = string.Empty;
        public string SampleKind { get; set; } = "split";
        public string Source { get; set; } = string.Empty;
        public Dictionary<string, string> Candidates { get; set; } = [];
        public Dictionary<string, GoldLabel> Labels { get; set; } = [];
    }

    public sealed class GoldLabel
    {
        public string Label { get; set; } = string.Empty;
        public List<string> DefectCategories { get; set; } = [];
        public string CorrectionSafety { get; set; } = string.Empty;
    }

    public sealed class CorrectionSample
    {
        public string SampleId { get; set; } = string.Empty;
        public string SampleKind { get; set; } = "split";
        public string Source { get; set; } = string.Empty;
        public string CurrentTranslation { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public List<string> DefectCategories { get; set; } = [];
        public string ProposedCorrection { get; set; } = string.Empty;
        public string CorrectionSafety { get; set; } = string.Empty;
    }

    public sealed class EvaluatorResultFile
    {
        public string ModelName { get; set; } = string.Empty;
        public string Status { get; set; } = "running";
        public string GoldSetFingerprint { get; set; } = string.Empty;
        public DateTime? CompletedAtUtc { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public List<EvaluatorResult> Results { get; set; } = [];

        public EvaluatorSummary ToSummary() => new()
        {
            ModelName = ModelName,
            Status = Status,
            SampleCount = Results.Count,
            ParseSuccessRate = Results.Count == 0 ? 0 : Results.Count(x => x.ParseSuccess) / (double)Results.Count,
            DetectionAccuracy = Results.Where(x => x.EvaluationKind == "detection").DefaultIfEmpty().Average(x => x == null ? 0 : x.ExpectedLabel == x.ActualLabel ? 1 : 0),
            CorrectionSafetyAccuracy = Results.Where(x => x.EvaluationKind == "correction").DefaultIfEmpty().Average(x => x == null ? 0 : x.ExpectedCorrectionSafety == x.ActualCorrectionSafety ? 1 : 0),
            AverageMilliseconds = Results.Count == 0 ? 0 : Results.Average(x => x.ElapsedMilliseconds),
            P95Milliseconds = Percentile(Results.Select(x => x.ElapsedMilliseconds), 0.95),
            ElapsedMilliseconds = ElapsedMilliseconds,
        };
    }

    public sealed class EvaluatorResult
    {
        public string ResultId { get; set; } = string.Empty;
        public string SampleId { get; set; } = string.Empty;
        public string SampleKind { get; set; } = string.Empty;
        public string EvaluationKind { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string CurrentTranslation { get; set; } = string.Empty;
        public string ProposedCorrection { get; set; } = string.Empty;
        public string ExpectedLabel { get; set; } = string.Empty;
        public string ActualLabel { get; set; } = string.Empty;
        public List<string> ExpectedDefectCategories { get; set; } = [];
        public string ActualDefectCategory { get; set; } = string.Empty;
        public string ExpectedCorrectionSafety { get; set; } = string.Empty;
        public string ActualCorrectionSafety { get; set; } = string.Empty;
        public bool ParseSuccess { get; set; }
        public int? Score { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }

    public sealed class EvaluatorSummary
    {
        public string ModelName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int SampleCount { get; set; }
        public double ParseSuccessRate { get; set; }
        public double DetectionAccuracy { get; set; }
        public double CorrectionSafetyAccuracy { get; set; }
        public double AverageMilliseconds { get; set; }
        public long P95Milliseconds { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }

    public sealed class EvaluatorComparison
    {
        public int SchemaVersion { get; set; }
        public string GoldSetFingerprint { get; set; } = string.Empty;
        public int EvaluatorCount { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public List<EvaluatorSummary> Evaluators { get; set; } = [];
    }

    private static long Percentile(IEnumerable<long> values, double percentile)
    {
        var ordered = values.OrderBy(x => x).ToList();
        if (ordered.Count == 0)
            return 0;
        return ordered[Math.Clamp((int)Math.Ceiling(ordered.Count * percentile) - 1, 0, ordered.Count - 1)];
    }
}