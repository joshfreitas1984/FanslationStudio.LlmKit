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
        // One client for the whole run - auth is per request (see TranslationService), so swapping
        // the resident model between phases never needs a fresh client.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };

        foreach (var modelName in modelNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!configuredModels.TryGetValue(modelName, out var model))
                throw new InvalidOperationException($"QC evaluator model '{modelName}' is not configured.");
            if (!model.Prompts.ContainsKey("BaseQualityReviewPrompt"))
                throw new InvalidOperationException($"QC evaluator model '{modelName}' has no BaseQualityReviewPrompt.");

            Console.WriteLine($"QC evaluator assessment: {modelName}");
            var report = await RunModelAsync(config, client, modelName, model, goldSet, fingerprint, outputDirectory);
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

        if (settings.CorrectorModelNames.Count > 0)
            await RunCorrectionGenerationComparisonAsync(config, client, settings, goldSet, fingerprint, outputDirectory, configuredModels);
    }

    /// <summary>
    /// Validates docs/plans/qc-fast-corrector-model-swap.md's theory: for every gold row with a
    /// known confirmed defect set, have each <see cref="QualityEvaluatorAssessmentConfig.CorrectorModelNames"/>
    /// candidate draft its OWN correction via <see cref="QualityReviewWorkflow.GenerateCorrectionAsync"/>
    /// (never reusing the gold set's human-authored <see cref="CorrectionSample.ProposedCorrection"/>,
    /// which exists for a different purpose - scoring verification, not generation), then score every
    /// draft's safety with <see cref="QualityEvaluatorAssessmentConfig.JudgeModelName"/> - a model
    /// never grades its own draft.
    ///
    /// Runs as two full phases per corrector model rather than interleaving generate/verify per row:
    /// phase A drafts every correction with only the corrector model resident, phase B scores every
    /// draft with only the judge model resident. This keeps this run's own latency numbers free of
    /// model-swap cost (measured separately - see the sibling swap-cost benchmark) and mirrors the
    /// "batch by phase" production shape this whole test exists to justify or rule out.
    /// </summary>
    internal static async Task RunCorrectionGenerationComparisonAsync(
        LlmConfig config,
        HttpClient client,
        QualityEvaluatorAssessmentConfig settings,
        GoldSet goldSet,
        string fingerprint,
        string outputDirectory,
        Dictionary<string, ModelExecutionConfig> configuredModels)
    {
        if (string.IsNullOrEmpty(settings.JudgeModelName))
            throw new InvalidOperationException("QualityEvaluatorAssessment.JudgeModelName is required when CorrectorModelNames is set.");
        if (!configuredModels.TryGetValue(settings.JudgeModelName, out var judgeModel))
            throw new InvalidOperationException($"QC evaluator judge model '{settings.JudgeModelName}' is not configured.");
        if (!judgeModel.Prompts.ContainsKey("BaseQualityReviewVerificationPrompt"))
            throw new InvalidOperationException($"QC evaluator judge model '{settings.JudgeModelName}' has no BaseQualityReviewVerificationPrompt.");

        var pool = BuildCorrectionGenerationPool(goldSet);
        if (pool.Count == 0)
        {
            Console.WriteLine("Correction-generation assessment: gold set has no Defect rows with known defect categories - nothing to draft against.");
            return;
        }

        var comparisonDirectory = Path.Combine(outputDirectory, settings.EnableRepairLoop ? "CorrectionGenerationWithRepair" : "CorrectionGeneration");
        Directory.CreateDirectory(comparisonDirectory);
        var summaries = new List<CorrectionGenerationSummary>();

        foreach (var correctorModelName in settings.CorrectorModelNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var selfJudged = string.Equals(correctorModelName, settings.JudgeModelName, StringComparison.OrdinalIgnoreCase);
            if (selfJudged)
                Console.WriteLine($"WARNING: '{correctorModelName}' is both corrector and judge for this run - it is grading its own draft. " +
                    "Treat the resulting safe rate as an optimistic upper bound (self-grading bias), not a trustworthy absolute number - " +
                    "useful only to check whether the correction-generation task itself is hard (a low self-graded score is a strong signal), " +
                    "not to certify this model's corrections as actually safe.");
            if (!configuredModels.TryGetValue(correctorModelName, out var correctorModel))
                throw new InvalidOperationException($"QC evaluator corrector model '{correctorModelName}' is not configured.");
            if (!correctorModel.Prompts.ContainsKey("BaseQualityReviewCorrectionPrompt"))
                throw new InvalidOperationException($"QC evaluator corrector model '{correctorModelName}' has no BaseQualityReviewCorrectionPrompt.");

            var modelDirectory = Path.Combine(comparisonDirectory, SafeName(correctorModelName));
            Directory.CreateDirectory(modelDirectory);
            var resultPath = Path.Combine(modelDirectory, "Results.yaml");
            var report = LoadOrCreateCorrectionGenerationReport(resultPath, correctorModelName, settings.JudgeModelName, fingerprint, pool);
            report.SelfJudged = selfJudged;

            if (report.Status != "completed")
            {
                Console.WriteLine($"Correction generation: {correctorModelName} drafting, {settings.JudgeModelName} judging ({report.Results.Count} rows)");

                config.Runtime.Models = new Dictionary<string, ModelExecutionConfig> { [correctorModelName] = correctorModel };
                foreach (var result in report.Results.Where(r => !r.GenerationAttempted))
                {
                    var sample = MaskSample(config, result.Source, result.CurrentTranslation);
                    var confirmedDefects = result.ConfirmedDefectCategories.Select(ParseCategory).Distinct().ToList();

                    var stopwatch = Stopwatch.StartNew();
                    result.ProposedCorrection = await QualityReviewWorkflow.GenerateCorrectionAsync(
                        config, correctorModel, client, result.SampleId, sample.MaskedRaw, sample.MaskedTranslated,
                        sample.GlossaryPrompt, confirmedDefects, null);
                    stopwatch.Stop();
                    result.GenerationAttempted = true;
                    result.GenerationElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                    if (result.ProposedCorrection == null)
                        result.ActualCorrectionSafety = "NoCorrectionProduced";
                    else
                    {
                        result.GateFailure = CheckGate(config, correctorModel, result);
                        if (result.GateFailure != null)
                            result.InitialCorrectionSafety = RejectedByGate;
                    }
                    WriteYamlAtomically(resultPath, report);
                }

                // Mirrors QualityReviewConfig.PreVerificationGateEnabled: a draft the validation
                // gate rejects is never judged - it is repaired against the gate's reason below, or
                // (no repair loop / budget) recorded as RejectedByGate, which production's final
                // gate would do too.
                await JudgePendingAsync(config, client, settings.JudgeModelName, judgeModel, report, resultPath, report.Results, recordInitial: true);

                // Mirrors GetLlmVerdictAsync's gate/verify/repair loop: a row the gate or the judge
                // rejected goes back through the SAME corrector model's GetCorrectionRepairAsync
                // (never the judge - a model never authors its own grade) with the gate's reason or
                // the judge's evidence quotes, then through the gate and the judge again against
                // the FULL original confirmed set, until RepairAttemptsUsed reaches
                // MaxScoreRepairIterations. A repair that returns NONE or the same text stops the
                // row. Each iteration is a two-phase pass (repair with only the corrector resident,
                // then judge with only the judge resident) so repeated iterations still cost ~2
                // swaps for the WHOLE batch, not per row - see the Twenty-second round's swap-cost
                // finding this design depends on.
                var maxRepairAttempts = settings.EnableRepairLoop ? Math.Max(0, config.QualityReview.MaxScoreRepairIterations) : 0;
                for (var attempt = 0; attempt < maxRepairAttempts; attempt++)
                {
                    var needsRepair = report.Results.Where(r => r.RepairAttemptsUsed < maxRepairAttempts && NeedsRepair(r)).ToList();
                    if (needsRepair.Count == 0)
                        break;
                    Console.WriteLine($"Correction generation: {correctorModelName} repair attempt {attempt + 1}/{maxRepairAttempts} for {needsRepair.Count} row(s)");

                    config.Runtime.Models = new Dictionary<string, ModelExecutionConfig> { [correctorModelName] = correctorModel };
                    foreach (var result in needsRepair)
                    {
                        await RepairAsync(config, client, correctorModel, result);
                        WriteYamlAtomically(resultPath, report);
                    }

                    await JudgePendingAsync(config, client, settings.JudgeModelName, judgeModel, report, resultPath, needsRepair, recordInitial: false);
                }

                foreach (var result in report.Results.Where(r => r.ActualCorrectionSafety == null && r.GateFailure != null))
                    result.ActualCorrectionSafety = RejectedByGate;

                report.Status = "completed";
                report.CompletedAtUtc = DateTime.UtcNow;
                WriteYamlAtomically(resultPath, report);
            }
            else
            {
                Console.WriteLine($"Correction-generation assessment for '{correctorModelName}' already completed - skipping.");
            }

            summaries.Add(report.ToSummary());
            WriteHumanReviewSample(modelDirectory, report);
        }

        WriteYamlAtomically(Path.Combine(comparisonDirectory, "Comparison.yaml"), new CorrectionGenerationComparison
        {
            SchemaVersion = 1,
            GoldSetFingerprint = fingerprint,
            JudgeModelName = settings.JudgeModelName,
            GeneratedAtUtc = DateTime.UtcNow,
            Correctors = summaries,
        });
    }

    /// <summary>
    /// One gold row's text masked for a QC prompt, the way production masks a column
    /// (<see cref="StringTokenReplacer"/>), plus its glossary block. Gold-set items have no
    /// output-file identity, so the glossary is scoped to string.Empty - this only affects glossary
    /// lines with "only"/"exclude" file restrictions, which are skipped here the same way they'd be
    /// skipped for any file not in an "only" list. Further text (a correction) must go through
    /// <see cref="Mask"/> after the source/translation, on the same replacer.
    /// </summary>
    private sealed record MaskedSample(StringTokenReplacer TokenReplacer, string GlossaryPrompt, string MaskedRaw, string MaskedTranslated)
    {
        public string Mask(string text) => TokenReplacer.Replace(text);
    }

    private static MaskedSample MaskSample(LlmConfig config, string source, string translation)
    {
        var tokenReplacer = new StringTokenReplacer();
        var glossaryPrompt = GlossaryLine.AppendPromptsFor(source, config.Runtime.GlossaryLines, string.Empty);
        var maskedRaw = tokenReplacer.Replace(source);
        var maskedTranslated = tokenReplacer.Replace(translation);
        return new MaskedSample(tokenReplacer, glossaryPrompt, maskedRaw, maskedTranslated);
    }

    /// <summary>The judge's call 4 on <paramref name="result"/>'s current draft, against its full confirmed set.</summary>
    private static async Task<(QcVerificationResult Verdict, long ElapsedMilliseconds)> JudgeCorrectionAsync(
        LlmConfig config, ModelExecutionConfig judgeModel, HttpClient client, CorrectionGenerationResult result)
    {
        var sample = MaskSample(config, result.Source, result.CurrentTranslation);
        var maskedCorrection = sample.Mask(result.ProposedCorrection!);
        var confirmedDefects = result.ConfirmedDefectCategories.Select(ParseCategory).Distinct().ToList();

        var stopwatch = Stopwatch.StartNew();
        var verdict = await QualityReviewWorkflow.GetVerificationVerdictAsync(
            config, judgeModel, client, result.SampleId, sample.MaskedRaw, sample.MaskedTranslated,
            sample.GlossaryPrompt, confirmedDefects, maskedCorrection);
        stopwatch.Stop();
        return (verdict, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Records a judge verdict on <paramref name="result"/>. UnresolvedDefectCategories is persisted
    /// on the result itself (not a local dictionary) so a crash/resume mid-repair-loop reconstructs
    /// exactly which rows still need fixing from Results.yaml alone - a local-only dictionary would
    /// silently forget any row whose Harmful verdict was already persisted before an interruption,
    /// since phase B only re-verifies rows with ActualCorrectionSafety == null.
    /// </summary>
    private static void ApplyJudgeVerdict(CorrectionGenerationResult result, QcVerificationResult verdict, int minAcceptableScore)
    {
        result.ParseSuccess = verdict.Success;
        result.VerificationScore = verdict.Score;
        result.ActualCorrectionSafety = ClassifyCorrectionSafety(verdict, minAcceptableScore);
        var toFix = result.ActualCorrectionSafety == "Harmful"
            ? verdict.UnresolvedDefects.Concat(verdict.NewDefects).Distinct().ToList()
            : [];
        result.UnresolvedDefectCategories = toFix.Select(QcDefectCategoryTokens.ToToken).ToList();
        result.VerifierEvidence = QualityReviewWorkflow.FormatVerifierEvidence(verdict, toFix);
    }

    /// <summary>A gold row's outcome when its draft fails the validation gate and is never repaired into a passing one.</summary>
    internal const string RejectedByGate = "RejectedByGate";

    /// <summary>
    /// Bump when the correction-generation pipeline changes what a row's result means, so a saved
    /// Results.yaml from the old pipeline is discarded instead of resumed or reported as current.
    /// 2: pre-verification gate, evidence-guided repairs, no-op repair stop, wire-token target categories.
    /// </summary>
    internal const int CorrectionGenerationPipelineVersion = 2;

    /// <summary>Gold rows have no output file; rule checks see an empty-path CSV file, as glossary scoping does in <see cref="MaskSample"/>.</summary>
    private static readonly TextFileToSplit GoldTextFile = new() { Path = string.Empty, TextFileType = TextFileType.RawCsv };

    /// <summary>The production pre-verification gate (<see cref="QualityReviewWorkflow.BuildCandidateValidator"/>) on <paramref name="result"/>'s current draft.</summary>
    private static string? CheckGate(LlmConfig config, ModelExecutionConfig model, CorrectionGenerationResult result)
    {
        var sample = MaskSample(config, result.Source, result.CurrentTranslation);
        var validator = QualityReviewWorkflow.BuildCandidateValidator(
            config, model, result.Source, result.CurrentTranslation, sample.TokenReplacer, GoldTextFile, 0);
        return validator?.Invoke(sample.Mask(result.ProposedCorrection!));
    }

    /// <summary>A gate failure with an actionable reason, or a judge rejection naming what to fix.</summary>
    private static bool NeedsRepair(CorrectionGenerationResult result) =>
        result.ActualCorrectionSafety == null && !string.IsNullOrWhiteSpace(result.GateFailure)
        || result.ActualCorrectionSafety == "Harmful" && result.UnresolvedDefectCategories.Count > 0;

    /// <summary>
    /// One repair call for <paramref name="result"/>: against the gate's reason when the draft failed
    /// the gate (all confirmed categories as targets), otherwise against the judge's unresolved/new
    /// categories and evidence. A NONE or unchanged answer ends the row's repairs, exactly as in
    /// <see cref="QualityReviewWorkflow.GetLlmVerdictAsync"/>; a new draft is re-gated and left unjudged.
    /// </summary>
    private static async Task RepairAsync(LlmConfig config, HttpClient client, ModelExecutionConfig correctorModel, CorrectionGenerationResult result)
    {
        var sample = MaskSample(config, result.Source, result.CurrentTranslation);
        var maskedPrevious = sample.Mask(result.ProposedCorrection!);
        var gateFailure = result.GateFailure;
        var targetDefects = gateFailure != null
            ? result.ConfirmedDefectCategories.Select(ParseCategory).Distinct().ToList()
            : result.UnresolvedDefectCategories.Select(QcDefectCategoryTokens.Parse).Distinct().ToList();

        var stopwatch = Stopwatch.StartNew();
        var repaired = await QualityReviewWorkflow.GetCorrectionRepairAsync(
            config, correctorModel, client, result.SampleId, sample.MaskedRaw, sample.MaskedTranslated,
            sample.GlossaryPrompt, targetDefects, maskedPrevious, null,
            ruleCheckFailure: gateFailure, verifierEvidence: gateFailure == null ? result.VerifierEvidence : null);
        stopwatch.Stop();
        result.RepairAttemptsUsed++;
        result.RepairElapsedMilliseconds = (result.RepairElapsedMilliseconds ?? 0) + stopwatch.ElapsedMilliseconds;

        if (repaired == null || string.Equals(repaired, maskedPrevious, StringComparison.Ordinal))
        {
            // Nothing further to attempt: a gate failure stays one (finalized as RejectedByGate after
            // the loop); a judged row keeps its Harmful verdict and leaves the repair queue.
            if (gateFailure != null)
                result.ActualCorrectionSafety = RejectedByGate;
            result.UnresolvedDefectCategories = [];
            return;
        }

        result.ProposedCorrection = repaired;
        result.ActualCorrectionSafety = null;
        result.UnresolvedDefectCategories = [];
        result.VerifierEvidence = null;
        result.GateFailure = CheckGate(config, correctorModel, result);
    }

    /// <summary>
    /// Judges every row in <paramref name="rows"/> that has a gate-passing, not-yet-judged draft, with
    /// only the judge model resident. <paramref name="recordInitial"/> marks the first pass, whose
    /// outcome is kept as <see cref="CorrectionGenerationResult.InitialCorrectionSafety"/>.
    /// </summary>
    private static async Task JudgePendingAsync(
        LlmConfig config, HttpClient client, string judgeModelName, ModelExecutionConfig judgeModel,
        CorrectionGenerationReportFile report, string resultPath, IEnumerable<CorrectionGenerationResult> rows, bool recordInitial)
    {
        config.Runtime.Models = new Dictionary<string, ModelExecutionConfig> { [judgeModelName] = judgeModel };
        foreach (var result in rows.Where(r => r.ProposedCorrection != null && r.ActualCorrectionSafety == null && r.GateFailure == null))
        {
            var (verdict, elapsedMilliseconds) = await JudgeCorrectionAsync(config, judgeModel, client, result);
            result.VerificationElapsedMilliseconds = (result.VerificationElapsedMilliseconds ?? 0) + elapsedMilliseconds;
            ApplyJudgeVerdict(result, verdict, config.QualityReview.MinAcceptableScore);
            if (recordInitial && result.InitialCorrectionSafety == null)
                result.InitialCorrectionSafety = result.ActualCorrectionSafety;
            WriteYamlAtomically(resultPath, report);
        }
    }

    /// <summary>Unscored (no parseable verdict), Harmful (rejected, or accepted below
    /// <paramref name="minAcceptableScore"/>), or Safe.</summary>
    internal static string ClassifyCorrectionSafety(QcVerificationResult verdict, int minAcceptableScore) =>
        !verdict.Success
            ? "Unscored"
            : !verdict.Accepted
                ? "Harmful"
                : verdict.Score >= minAcceptableScore ? "Safe" : "Harmful";

    /// <summary>
    /// Every gold row with a known confirmed defect set to draft a fresh correction against: gold
    /// Items whose OWN candidate is labeled Defect (skips Pass/Abstain rows and any candidate not
    /// itself the labeled one), plus every CorrectionSample (its human-authored ProposedCorrection is
    /// intentionally ignored here - see the caller's summary). A row with zero listed categories still
    /// counts (falls back to <see cref="QcDefectCategory.OtherNamedDefect"/> via <see cref="ParseCategory"/>
    /// at draft time), matching <see cref="ReviewCorrectionAsync"/>'s existing fallback.
    /// </summary>
    private static List<CorrectionGenerationResult> BuildCorrectionGenerationPool(GoldSet goldSet)
    {
        var pool = new List<CorrectionGenerationResult>();
        foreach (var item in goldSet.Items)
        {
            foreach (var candidate in item.Candidates)
            {
                if (!item.Labels.TryGetValue(candidate.Key, out var label) || label.Label != "Defect")
                    continue;
                pool.Add(new CorrectionGenerationResult
                {
                    SampleId = $"{item.SampleId}:{candidate.Key}",
                    SampleKind = item.SampleKind,
                    Source = item.Source,
                    CurrentTranslation = candidate.Value,
                    ConfirmedDefectCategories = label.DefectCategories,
                });
            }
        }
        foreach (var item in goldSet.CorrectionSamples)
        {
            pool.Add(new CorrectionGenerationResult
            {
                SampleId = item.SampleId,
                SampleKind = item.SampleKind,
                Source = item.Source,
                CurrentTranslation = item.CurrentTranslation,
                ConfirmedDefectCategories = item.DefectCategories,
            });
        }
        return pool;
    }

    private static CorrectionGenerationReportFile LoadOrCreateCorrectionGenerationReport(
        string path, string correctorModelName, string judgeModelName, string fingerprint, List<CorrectionGenerationResult> pool)
    {
        if (File.Exists(path))
        {
            var existing = YamlHelper.CreateDeserializer().Deserialize<CorrectionGenerationReportFile>(File.ReadAllText(path));
            if (existing != null
                && existing.CorrectorModelName == correctorModelName
                && existing.JudgeModelName == judgeModelName
                && existing.GoldSetFingerprint == fingerprint
                && existing.PipelineVersion == CorrectionGenerationPipelineVersion
                && existing.Results.Count == pool.Count)
            {
                return existing;
            }
        }
        return new CorrectionGenerationReportFile
        {
            CorrectorModelName = correctorModelName,
            JudgeModelName = judgeModelName,
            GoldSetFingerprint = fingerprint,
            PipelineVersion = CorrectionGenerationPipelineVersion,
            Results = pool.Select(item => new CorrectionGenerationResult
            {
                SampleId = item.SampleId,
                SampleKind = item.SampleKind,
                Source = item.Source,
                CurrentTranslation = item.CurrentTranslation,
                ConfirmedDefectCategories = item.ConfirmedDefectCategories,
            }).ToList(),
        };
    }

    /// <summary>
    /// No automated ground truth exists for whether a drafted correction actually resolves the
    /// confirmed defect (only whether it's safe) - per docs/plans/qc-fast-corrector-model-swap.md's
    /// "What validating this would take" step 3, that needs human review. Writes every row (source,
    /// current translation, confirmed defects, the draft, and the judge's safety verdict) to a plain
    /// YAML file a human can read start to finish, instead of only a sample - the gold-row count here
    /// is small enough (dozens, not thousands) that full review is cheap.
    /// </summary>
    private static void WriteHumanReviewSample(string modelDirectory, CorrectionGenerationReportFile report)
    {
        var reviewPath = Path.Combine(modelDirectory, "HumanReview.yaml");
        var rows = report.Results.Select(r => new
        {
            r.SampleId,
            r.ConfirmedDefectCategories,
            r.Source,
            r.CurrentTranslation,
            r.ProposedCorrection,
            InitialJudgeSafety = r.InitialCorrectionSafety,
            JudgeSafety = r.ActualCorrectionSafety,
            JudgeScore = r.VerificationScore,
            r.RepairAttemptsUsed,
            CompletenessReviewed = false,
            CompletenessNote = "",
        });
        File.WriteAllText(reviewPath, YamlHelper.CreateSerializer().Serialize(rows), Encoding.UTF8);
    }

    private static async Task<EvaluatorResultFile> RunModelAsync(
        LlmConfig config,
        HttpClient client,
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
        var stopwatch = Stopwatch.StartNew();
        var completedResultIds = report.Results.Select(x => x.ResultId).ToHashSet();

        foreach (var item in goldSet.Items)
        {
            foreach (var candidate in item.Candidates)
            {
                if (!item.Labels.TryGetValue(candidate.Key, out var expected))
                    continue;

                var resultId = $"detection:{item.SampleId}:{candidate.Key}:{modelName}";
                if (completedResultIds.Contains(resultId))
                    continue;

                report.Results.Add(await ReviewDetectionAsync(config, model, client, resultId,
                    item.SampleId, item.SampleKind, item.Source, candidate.Value,
                    expected, config.QualityEvaluatorAssessment.DoubledDetection,
                    config.QualityEvaluatorAssessment.DetectionThinkingEnabled));
                WriteYamlAtomically(resultPath, report);
            }
        }

        foreach (var item in goldSet.CorrectionSamples)
        {
            var resultId = $"correction:{item.SampleId}:{modelName}";
            if (completedResultIds.Contains(resultId))
                continue;

            report.Results.Add(await ReviewCorrectionAsync(config, model, client, resultId, item, modelName,
                config.QualityEvaluatorAssessment.DoubledVerification));
            WriteYamlAtomically(resultPath, report);
        }

        stopwatch.Stop();
        report.Status = "completed";
        report.CompletedAtUtc = DateTime.UtcNow;
        report.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        WriteYamlAtomically(resultPath, report);
        return report;
    }

    /// <summary>
    /// Calls <see cref="QualityReviewWorkflow.DetectDefectsAsync"/> directly (once, or twice merged
    /// via <see cref="QcDetectionResult.Merge"/> when <paramref name="doubledDetection"/> is true)
    /// rather than the full five-call <see cref="QualityReviewWorkflow.GetLlmVerdictAsync"/>
    /// orchestrator - detection is the only thing scored here, so calls 3-5 (correction draft/
    /// verify/repair) would only contaminate elapsed-time measurements without affecting the score.
    /// See docs/plans/qc-evaluator-comparison.md's "Handoff: Implementing Process Variants" section.
    /// The classification below (None/Uncertain/named -> Pass/Abstain/Defect) mirrors
    /// <see cref="QualityReviewWorkflow.GetLlmVerdictAsync"/>'s own mapping up to the point where it
    /// would start drafting a correction.
    /// </summary>
    private static async Task<EvaluatorResult> ReviewDetectionAsync(
        LlmConfig config,
        ModelExecutionConfig model,
        HttpClient client,
        string resultId,
        string sampleId,
        string sampleKind,
        string source,
        string translation,
        GoldLabel expected,
        bool doubledDetection,
        bool enableThinking = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var sample = MaskSample(config, source, translation);

        var call1 = await QualityReviewWorkflow.DetectDefectsAsync(config, model, client, source, sample.MaskedRaw, sample.MaskedTranslated, sample.GlossaryPrompt, null, enableThinking);
        var confirmed = call1;
        if (doubledDetection && call1.Success)
        {
            var call2 = await QualityReviewWorkflow.DetectDefectsAsync(config, model, client, source, sample.MaskedRaw, sample.MaskedTranslated, sample.GlossaryPrompt, null, enableThinking);
            confirmed = QcDetectionResult.Merge(call1, call2);
        }
        stopwatch.Stop();

        string actual;
        QcDefectCategory actualCategory;
        List<string> actualCategories;
        if (!confirmed.Success)
        {
            actual = "Unscored";
            actualCategory = QcDefectCategory.Unknown;
            actualCategories = [];
        }
        else if (!confirmed.HasDefects)
        {
            actual = "Pass";
            actualCategory = QcDefectCategory.None;
            actualCategories = [];
        }
        else
        {
            var isUncertain = confirmed.Findings.Any(finding => finding.Category == QcDefectCategory.Uncertain);
            var namedDefects = confirmed.Findings
                .Where(finding => finding.Category != QcDefectCategory.Uncertain)
                .Select(finding => finding.Category)
                .ToList();
            actual = namedDefects.Count == 0 ? "Abstain" : "Defect";
            actualCategory = namedDefects.Count == 0 ? QcDefectCategory.Uncertain : namedDefects[0];
            actualCategories = namedDefects
                .Select(category => category.ToString())
                .Concat(isUncertain ? ["Uncertain"] : [])
                .Distinct()
                .ToList();
        }

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
            ActualDefectCategory = actualCategory.ToString(),
            ActualDefectCategories = actualCategories,
            ParseSuccess = confirmed.Success,
            FailureKind = confirmed.Success ? null : confirmed.FailureKind.ToString(),
            FailureDetail = confirmed.Success ? null : confirmed.FailureDetail,
            Score = actual == "Pass" ? 100 : null,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Under the five-call redesign, call 4 (<see cref="QualityReviewWorkflow.GetVerificationVerdictAsync"/>)
    /// only checks whether a correction resolves an ALREADY-CONFIRMED defect set and introduces no
    /// new one - it no longer re-litigates whether the claimed defect exists at all (that judgment
    /// moved entirely to calls 1/2's detection/merge). So <paramref name="item"/>'s gold-labeled
    /// <see cref="CorrectionSample.DefectCategories"/> are treated here as already-confirmed ground
    /// truth, not a claim to verify - this method measures correction safety only, not detection
    /// accuracy. The old "Unnecessary" bucket (the claimed defect didn't hold up) has no equivalent
    /// at this stage anymore; a full per-stage assessor that also re-runs detection independently is
    /// tracked as a follow-up (see docs/plans/qc-evaluator-comparison.md).
    ///
    /// Variant 2 (doubled verification, see docs/plans/qc-evaluator-comparison.md's "Process
    /// Variants"): when <paramref name="doubledVerification"/> is true, runs
    /// <see cref="QualityReviewWorkflow.GetVerificationVerdictAsync"/> twice (fresh, independent
    /// calls) and combines them via <see cref="QcVerificationResult.Merge"/>, which merges strictly
    /// (either call's objection rejects the correction) - the opposite direction from doubled
    /// detection's permissive merge, since here the worse failure is accepting a harmful correction,
    /// not rejecting a safe one.
    /// </summary>
    private static async Task<EvaluatorResult> ReviewCorrectionAsync(
        LlmConfig config,
        ModelExecutionConfig model,
        HttpClient client,
        string resultId,
        CorrectionSample item,
        string modelName,
        bool doubledVerification)
    {
        var stopwatch = Stopwatch.StartNew();
        var confirmedDefects = item.DefectCategories.Count == 0
            ? [QcDefectCategory.OtherNamedDefect]
            : item.DefectCategories.Select(ParseCategory).Distinct().ToList();
        var sample = MaskSample(config, item.Source, item.CurrentTranslation);
        var maskedCorrection = sample.Mask(item.ProposedCorrection);

        QcVerificationResult verdict;
        if (!model.Prompts.ContainsKey("BaseQualityReviewVerificationPrompt"))
        {
            verdict = new QcVerificationResult(false, [], [], 0);
        }
        else
        {
            var call1 = await QualityReviewWorkflow.GetVerificationVerdictAsync(config, model, client, item.Source,
                sample.MaskedRaw, sample.MaskedTranslated, sample.GlossaryPrompt, confirmedDefects, maskedCorrection);
            verdict = call1;
            if (doubledVerification && call1.Success)
            {
                var call2 = await QualityReviewWorkflow.GetVerificationVerdictAsync(config, model, client, item.Source,
                    sample.MaskedRaw, sample.MaskedTranslated, sample.GlossaryPrompt, confirmedDefects, maskedCorrection);
                verdict = QcVerificationResult.Merge(call1, call2);
            }
        }
        stopwatch.Stop();

        var actualSafety = ClassifyCorrectionSafety(verdict, config.QualityReview.MinAcceptableScore);
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
            ActualLabel = verdict.Accepted ? "Pass" : "Defect",
            ExpectedDefectCategories = item.DefectCategories,
            ActualDefectCategory = string.Join(",", confirmedDefects),
            ExpectedCorrectionSafety = item.CorrectionSafety,
            ActualCorrectionSafety = actualSafety,
            ParseSuccess = verdict.Success,
            Score = verdict.Score,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Maps the gold set's free-form kebab-case category vocabulary (see
    /// docs/plans/qc-evaluator-comparison.md's "Validation" section) onto the production
    /// <see cref="QcDefectCategory"/> enum. Every category actually used in
    /// <c>Files/Goldset/GoldSet.yaml</c> is listed explicitly here - a category silently falling
    /// through to <see cref="QcDefectCategory.OtherNamedDefect"/> corrupts per-category
    /// precision/recall (see the taxonomy-mismatch gap this fixed). "formatting", "garbage-output",
    /// "fluency", "invented-tag", and "prompt-leak" are deliberately kept mapped to
    /// <see cref="QcDefectCategory.OtherNamedDefect"/> - they genuinely have no closer match in the
    /// production category list, not because nobody looked.
    /// </summary>
    internal static QcDefectCategory ParseCategory(string? category) => category?.ToLowerInvariant() switch
    {
        "dropped-content" => QcDefectCategory.DroppedContent,
        "pronoun-attribution" => QcDefectCategory.DroppedContent,
        "domain-term" or "terminology" => QcDefectCategory.DomainTerm,
        "lost-idiom" => QcDefectCategory.LostIdiom,
        "untranslated-pinyin" => QcDefectCategory.UntranslatedPinyin,
        "garbled-number" => QcDefectCategory.GarbledNumber,
        "hard-to-parse-seam" or "omitted-separator" => QcDefectCategory.HardToParseSeam,
        "literal-newline" or "misplaced-separator" => QcDefectCategory.HardToParseSeam,
        "unnatural-phrasing" => QcDefectCategory.UnnaturalPhrasing,
        "mistranslation" => QcDefectCategory.OtherNamedDefect,
        "formatting" or "garbage-output" or "fluency" or "invented-tag" or "prompt-leak" => QcDefectCategory.OtherNamedDefect,
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
        var directory = Path.GetDirectoryName(path)!;
        var serialized = YamlHelper.CreateSerializer().Serialize(value);
        // Retries a transient Windows sharing violation (antivirus/indexer briefly opening the file
        // right after a write, or an editor/IDE watching the output directory) rather than failing
        // the whole multi-minute run on one unlucky write - this result file is rewritten after every
        // single LLM call in this workflow, so it's hit often enough for a rare transient lock to show
        // up in practice (observed repeatedly during the correction-generation repair-loop rounds).
        // DirectoryNotFoundException is the same class of transient interference (observed against
        // this output directory specifically, likely antivirus/indexer briefly removing then
        // recreating a just-written directory) - recreate the directory before retrying rather than
        // failing the whole run.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(temporaryPath, serialized, Encoding.UTF8);
                File.Move(temporaryPath, path, true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
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

        public EvaluatorSummary ToSummary()
        {
            var detection = Results.Where(x => x.EvaluationKind == "detection").ToList();
            var scoredDetection = detection.Where(x => x.ParseSuccess).ToList();
            var corrections = Results.Where(x => x.EvaluationKind == "correction").ToList();
            var detectionCorrect = scoredDetection.Count(x => x.ExpectedLabel == x.ActualLabel);
            var expectedDefects = scoredDetection.Count(x => x.ExpectedLabel == "Defect");
            var actualDefects = scoredDetection.Count(x => x.ActualLabel == "Defect");
            var trueDefects = scoredDetection.Count(x => x.ExpectedLabel == "Defect" && x.ActualLabel == "Defect");
            var categoryJudgments = scoredDetection.Where(x => x.ExpectedLabel == "Defect" && x.ActualLabel == "Defect");

            return new EvaluatorSummary
            {
                ModelName = ModelName,
                Status = Status,
                SampleCount = Results.Count,
                DetectionSampleCount = detection.Count,
                CorrectionSampleCount = corrections.Count,
                ParseSuccessRate = Results.Count == 0 ? 0 : Results.Count(x => x.ParseSuccess) / (double)Results.Count,
                DetectionAccuracy = scoredDetection.Count == 0 ? 0 : detectionCorrect / (double)scoredDetection.Count,
                DetectionAccuracyIncludingUnscored = detection.Count == 0 ? 0 : detectionCorrect / (double)detection.Count,
                DetectionUnscoredCount = detection.Count(x => !x.ParseSuccess),
                DetectionUnscoredByFailureKind = detection
                    .Where(x => !x.ParseSuccess)
                    .GroupBy(x => x.FailureKind ?? nameof(QcDetectionFailureKind.ParseError))
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count()),
                DetectionAbstainCount = detection.Count(x => x.ActualLabel == "Abstain"),
                DetectionPassCount = detection.Count(x => x.ActualLabel == "Pass"),
                DetectionDefectCount = actualDefects,
                TrueDefectCount = trueDefects,
                FalsePositiveCount = scoredDetection.Count(x => x.ExpectedLabel != "Defect" && x.ActualLabel == "Defect"),
                FalseNegativeCount = scoredDetection.Count(x => x.ExpectedLabel == "Defect" && x.ActualLabel != "Defect"),
                DefectRecall = expectedDefects == 0 ? 0 : trueDefects / (double)expectedDefects,
                DefectPrecision = actualDefects == 0 ? 0 : trueDefects / (double)actualDefects,
                DefectCategoryAccuracy = categoryJudgments.Any()
                    ? categoryJudgments.Count(x => x.ExpectedDefectCategories.Any(expected =>
                        string.Equals(expected, x.ActualDefectCategory, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ParseCategory(expected).ToString(), x.ActualDefectCategory, StringComparison.OrdinalIgnoreCase))) / (double)categoryJudgments.Count()
                    : 0,
                CorrectionSafetyAccuracy = corrections.Count == 0 ? 0 : corrections.Count(x => string.Equals(x.ExpectedCorrectionSafety, x.ActualCorrectionSafety, StringComparison.OrdinalIgnoreCase)) / (double)corrections.Count,
                CorrectionSafeCount = corrections.Count(x => x.ActualCorrectionSafety == "Safe"),
                CorrectionHarmfulCount = corrections.Count(x => x.ActualCorrectionSafety == "Harmful"),
                CorrectionUnnecessaryCount = corrections.Count(x => x.ActualCorrectionSafety == "Unnecessary"),
                CorrectionUnscoredCount = corrections.Count(x => x.ActualCorrectionSafety == "Unscored"),
                AverageMilliseconds = Results.Count == 0 ? 0 : Results.Average(x => x.ElapsedMilliseconds),
                P95Milliseconds = Percentile(Results.Select(x => x.ElapsedMilliseconds), 0.95),
                ElapsedMilliseconds = ElapsedMilliseconds,
            };
        }
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
        public List<string> ActualDefectCategories { get; set; } = [];
        public string ExpectedCorrectionSafety { get; set; } = string.Empty;
        public string ActualCorrectionSafety { get; set; } = string.Empty;
        public bool ParseSuccess { get; set; }
        /// <summary>Detection only, null on success: RequestError/Truncated/ParseError - see
        /// <see cref="QcDetectionFailureKind"/>. RequestError/Truncated almost always mean the prompt
        /// outgrew the model's num_ctx rather than the model misbehaving.</summary>
        public string? FailureKind { get; set; }
        /// <summary>Detection only, null on success: the HTTP error message (RequestError) or the
        /// raw model response that failed to parse (Truncated/ParseError).</summary>
        public string? FailureDetail { get; set; }
        public int? Score { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }

    public sealed class EvaluatorSummary
    {
        public string ModelName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int SampleCount { get; set; }
        public int DetectionSampleCount { get; set; }
        public int CorrectionSampleCount { get; set; }
        public double ParseSuccessRate { get; set; }
        public double DetectionAccuracy { get; set; }
        public double DetectionAccuracyIncludingUnscored { get; set; }
        public int DetectionUnscoredCount { get; set; }
        public Dictionary<string, int> DetectionUnscoredByFailureKind { get; set; } = [];
        public int DetectionAbstainCount { get; set; }
        public int DetectionPassCount { get; set; }
        public int DetectionDefectCount { get; set; }
        public int TrueDefectCount { get; set; }
        public int FalsePositiveCount { get; set; }
        public int FalseNegativeCount { get; set; }
        public double DefectRecall { get; set; }
        public double DefectPrecision { get; set; }
        public double DefectCategoryAccuracy { get; set; }
        public double CorrectionSafetyAccuracy { get; set; }
        public int CorrectionSafeCount { get; set; }
        public int CorrectionHarmfulCount { get; set; }
        public int CorrectionUnnecessaryCount { get; set; }
        public int CorrectionUnscoredCount { get; set; }
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

    public sealed class CorrectionGenerationResult
    {
        public string SampleId { get; set; } = string.Empty;
        public string SampleKind { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string CurrentTranslation { get; set; } = string.Empty;
        public List<string> ConfirmedDefectCategories { get; set; } = [];
        public bool GenerationAttempted { get; set; }
        public string? ProposedCorrection { get; set; }
        public long? GenerationElapsedMilliseconds { get; set; }
        public bool ParseSuccess { get; set; }
        public int? VerificationScore { get; set; }
        public long? VerificationElapsedMilliseconds { get; set; }

        /// <summary>Safe/Harmful/Unscored/NoCorrectionProduced/RejectedByGate, or null while not yet judged.</summary>
        public string? ActualCorrectionSafety { get; set; }

        /// <summary>
        /// The validation gate's reason for rejecting the current draft (see
        /// <see cref="QualityReviewConfig.PreVerificationGateEnabled"/>), or null when it passes. Persisted
        /// so a resumed run knows which rows still need a gate repair instead of a judge call.
        /// </summary>
        public string? GateFailure { get; set; }

        /// <summary>The judge's evidence quotes for <see cref="UnresolvedDefectCategories"/>, sent to the next repair.</summary>
        public string? VerifierEvidence { get; set; }

        /// <summary>
        /// The FIRST verify call's outcome, captured before any repair loop runs (only meaningfully
        /// different from <see cref="ActualCorrectionSafety"/> when <see cref="QualityEvaluatorAssessmentConfig.EnableRepairLoop"/>
        /// is true) - lets a summary report both "how often does the first draft land safe" and "how
        /// often does the row end up safe after repair" from the same run.
        /// </summary>
        public string? InitialCorrectionSafety { get; set; }

        public int RepairAttemptsUsed { get; set; }
        public long? RepairElapsedMilliseconds { get; set; }

        /// <summary>
        /// Persisted (not a local dictionary) so a crash/resume mid-repair-loop reconstructs exactly
        /// which rows still need fixing, and with which target categories, from Results.yaml alone -
        /// see the caller's comment on the bug this replaced. Empty means "not currently in the
        /// repair loop" - either never Harmful, or Harmful but the corrector gave up (returned null)
        /// and there is nothing further to attempt.
        /// </summary>
        public List<string> UnresolvedDefectCategories { get; set; } = [];
    }

    public sealed class CorrectionGenerationReportFile
    {
        public string CorrectorModelName { get; set; } = string.Empty;
        public string JudgeModelName { get; set; } = string.Empty;
        public string Status { get; set; } = "running";
        public string GoldSetFingerprint { get; set; } = string.Empty;

        /// <summary>See <see cref="CorrectionGenerationPipelineVersion"/>; 0 for a file written before it existed.</summary>
        public int PipelineVersion { get; set; }

        public DateTime? CompletedAtUtc { get; set; }

        /// <summary>
        /// True when CorrectorModelName == JudgeModelName - the model graded its own draft. Kept
        /// visible on the report/summary rather than only in a console warning, so a saved
        /// Comparison.yaml can't be misread later as an independently-judged number. See the
        /// caller's console warning for the full caveat.
        /// </summary>
        public bool SelfJudged { get; set; }

        public List<CorrectionGenerationResult> Results { get; set; } = [];

        public CorrectionGenerationSummary ToSummary()
        {
            var scored = Results.Where(r => r.ActualCorrectionSafety != null).ToList();
            var initialScored = Results.Where(r => r.InitialCorrectionSafety != null).ToList();
            var repaired = Results.Where(r => r.RepairAttemptsUsed > 0).ToList();
            return new CorrectionGenerationSummary
            {
                CorrectorModelName = CorrectorModelName,
                SelfJudged = SelfJudged,
                Status = Status,
                SampleCount = Results.Count,
                NoCorrectionProducedCount = scored.Count(r => r.ActualCorrectionSafety == "NoCorrectionProduced"),
                SafeCount = scored.Count(r => r.ActualCorrectionSafety == "Safe"),
                HarmfulCount = scored.Count(r => r.ActualCorrectionSafety == "Harmful"),
                UnscoredCount = scored.Count(r => r.ActualCorrectionSafety == "Unscored"),
                RejectedByGateCount = scored.Count(r => r.ActualCorrectionSafety == RejectedByGate),
                SafeRate = scored.Count == 0 ? 0 : scored.Count(r => r.ActualCorrectionSafety == "Safe") / (double)scored.Count,
                InitialSafeRate = initialScored.Count == 0 ? 0 : initialScored.Count(r => r.InitialCorrectionSafety == "Safe") / (double)initialScored.Count,
                RepairedRowCount = repaired.Count,
                RepairRescuedCount = repaired.Count(r => r.InitialCorrectionSafety != "Safe" && r.ActualCorrectionSafety == "Safe"),
                RepairStillHarmfulCount = repaired.Count(r => r.ActualCorrectionSafety == "Harmful"),
                AverageRepairAttempts = repaired.Count == 0 ? 0 : repaired.Average(r => r.RepairAttemptsUsed),
                AverageGenerationMilliseconds = Results.Count(r => r.GenerationElapsedMilliseconds.HasValue) == 0
                    ? 0 : Results.Where(r => r.GenerationElapsedMilliseconds.HasValue).Average(r => r.GenerationElapsedMilliseconds!.Value),
                AverageVerificationMilliseconds = Results.Count(r => r.VerificationElapsedMilliseconds.HasValue) == 0
                    ? 0 : Results.Where(r => r.VerificationElapsedMilliseconds.HasValue).Average(r => r.VerificationElapsedMilliseconds!.Value),
                AverageRepairMilliseconds = repaired.Count == 0 ? 0 : repaired.Average(r => r.RepairElapsedMilliseconds ?? 0),
            };
        }
    }

    public sealed class CorrectionGenerationSummary
    {
        public string CorrectorModelName { get; set; } = string.Empty;
        public bool SelfJudged { get; set; }
        public string Status { get; set; } = string.Empty;
        public int SampleCount { get; set; }
        public int NoCorrectionProducedCount { get; set; }
        public int SafeCount { get; set; }
        public int HarmfulCount { get; set; }
        public int UnscoredCount { get; set; }

        /// <summary>Rows whose final draft still failed the validation gate - production would reject the correction outright.</summary>
        public int RejectedByGateCount { get; set; }

        /// <summary>Final safe rate - after the repair loop, when enabled. Identical to InitialSafeRate when EnableRepairLoop is false.</summary>
        public double SafeRate { get; set; }

        /// <summary>Safe rate of the FIRST draft, before any repair attempt.</summary>
        public double InitialSafeRate { get; set; }

        /// <summary>Rows that entered the repair loop at least once (judged Harmful, or rejected by the gate).</summary>
        public int RepairedRowCount { get; set; }

        /// <summary>Of the repaired rows, how many ended up Safe - the repair loop's actual rescue rate.</summary>
        public int RepairRescuedCount { get; set; }

        /// <summary>Of the repaired rows, how many were STILL Harmful after exhausting the repair budget.</summary>
        public int RepairStillHarmfulCount { get; set; }

        public double AverageRepairAttempts { get; set; }
        public double AverageGenerationMilliseconds { get; set; }
        public double AverageVerificationMilliseconds { get; set; }
        public double AverageRepairMilliseconds { get; set; }
    }

    public sealed class CorrectionGenerationComparison
    {
        public int SchemaVersion { get; set; }
        public string GoldSetFingerprint { get; set; } = string.Empty;
        public string JudgeModelName { get; set; } = string.Empty;
        public DateTime GeneratedAtUtc { get; set; }
        public List<CorrectionGenerationSummary> Correctors { get; set; } = [];
    }

    private static long Percentile(IEnumerable<long> values, double percentile)
    {
        var ordered = values.OrderBy(x => x).ToList();
        if (ordered.Count == 0)
            return 0;
        return ordered[Math.Clamp((int)Math.Ceiling(ordered.Count * percentile) - 1, 0, ordered.Count - 1)];
    }
}