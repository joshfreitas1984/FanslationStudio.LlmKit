using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;
using System.Net;
using System.Text;
using System.Text.Json;
using static FanslationStudio.LlmKit.Workflow.QualityEvaluatorAssessmentWorkflow;

namespace Tests.Workflow;

/// <summary>
/// The QC evaluator's correction-generation repair loop must mirror production's
/// <see cref="QualityReviewWorkflow.GetLlmVerdictAsync"/>: gate before judging, gate reason / judge
/// evidence into the repair, real defect tokens as repair targets, and an unchanged repair stops the row.
/// </summary>
public sealed class QcEvaluatorCorrectionLoopTests : IDisposable
{
    private const string Source = "{0}在{1}与{2}切磋武艺，最终{3}技高一筹。";
    private const string Translation = "{0} In {1} And {2} Sparring, in the end {3} One notch above";
    private const string DropsPlaceholder = "{0} spars with {1} and {2}, proving to be the superior fighter.";
    private const string Fixed = "{0} spars with {1} and {2}, and {3} proves the superior fighter.";
    private const string Accepted = "UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 95";

    private readonly string outputDirectory = Path.Combine(Path.GetTempPath(), "QcEvaluatorLoopTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);
    }

    /// <summary>Answers by call type (read from the system prompt), recording each call's user prompt in order.</summary>
    private sealed class ScriptedHandler(string correction, Func<int, string> repairReply, Func<int, string> verificationReply) : HttpMessageHandler
    {
        public List<(string Call, string User)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var messages = JsonDocument.Parse(body).RootElement.GetProperty("messages");
            var system = messages[0].GetProperty("content").GetString()!;
            Requests.Add((system, messages[1].GetProperty("content").GetString()!));
            var n = Requests.Count(r => r.Call == system);

            var reply = system switch
            {
                "correction" => $"CORRECTED: {correction}",
                "repair" => repairReply(n),
                "verification" or "verification-evidence" => verificationReply(n),
                _ => throw new InvalidOperationException(system),
            };
            var json = JsonSerializer.Serialize(new { message = new { content = reply }, done_reason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static ModelExecutionConfig BuildModel() => new()
    {
        Url = "http://test.local/api/chat",
        ApiKeyRequired = false,
        Model = "test-model",
        ModelParams = new Dictionary<string, object> { ["temperature"] = 0.15 },
        Prompts = new Dictionary<string, string>
        {
            ["BaseQualityReviewCorrectionPrompt"] = "correction",
            ["BaseQualityReviewCorrectionRepairPrompt"] = "repair",
            ["BaseQualityReviewVerificationPrompt"] = "verification",
            ["BaseQualityReviewVerificationEvidencePrompt"] = "verification-evidence",
            ["CorrectRemovalPrompt"] = "Restore `{0}` to the translation, as it was incorrectly removed.",
            ["CorrectAdditionalPrompt"] = "`{0}` appears in the result but not in the original text.",
            ["CorrectAlternativesPrompt"] = "Alternatives were provided.",
            ["CorrectExplainationPrompt"] = "Explanation added.",
            ["CorrectChinesePrompt"] = "Chinese left.",
            ["CorrectRemovedQuotesPrompt"] = "Quotes removed.",
        },
    };

    private async Task<(CorrectionGenerationResult Row, ScriptedHandler Handler)> RunAsync(
        string correction, Func<int, string> repairReply, Func<int, string> verificationReply, bool evidence = false)
    {
        var config = new LlmConfig
        {
            QualityReview = new QualityReviewConfig
            {
                Enabled = true,
                MaxScoreRepairIterations = 1,
                VerificationEvidenceEnabled = evidence,
            },
        };
        var settings = new QualityEvaluatorAssessmentConfig
        {
            CorrectorModelNames = ["Corrector"],
            JudgeModelName = "Judge",
            EnableRepairLoop = true,
        };
        var goldSet = new GoldSet
        {
            CorrectionSamples =
            [
                new CorrectionSample { SampleId = "s1", Source = Source, CurrentTranslation = Translation, DefectCategories = ["unnatural-phrasing"] },
            ],
        };
        var models = new Dictionary<string, ModelExecutionConfig> { ["Corrector"] = BuildModel(), ["Judge"] = BuildModel() };
        var handler = new ScriptedHandler(correction, repairReply, verificationReply);
        using var client = new HttpClient(handler);

        await RunCorrectionGenerationComparisonAsync(config, client, settings, goldSet, "fp", outputDirectory, models);

        var resultsPath = Path.Combine(outputDirectory, "CorrectionGenerationWithRepair", "Corrector", "Results.yaml");
        var report = YamlHelper.CreateDeserializer().Deserialize<CorrectionGenerationReportFile>(File.ReadAllText(resultsPath));
        Assert.Equal(CorrectionGenerationPipelineVersion, report.PipelineVersion);
        return (Assert.Single(report.Results), handler);
    }

    [Fact(DisplayName = "A draft that drops a placeholder is repaired with the gate's reason before it is judged")]
    public async Task GateFailure_RepairedBeforeJudging()
    {
        var (row, handler) = await RunAsync(DropsPlaceholder, _ => $"CORRECTED: {Fixed}", _ => Accepted);

        Assert.Equal(["correction", "repair", "verification"], handler.Requests.Select(r => r.Call));
        Assert.Contains("RULE CHECK FAILURE: Restore `{3}`", handler.Requests[1].User);
        Assert.Equal(RejectedByGate, row.InitialCorrectionSafety);
        Assert.Equal("Safe", row.ActualCorrectionSafety);
    }

    [Fact(DisplayName = "A gate failure the repair cannot fix is RejectedByGate and never judged")]
    public async Task UnfixableGateFailure_IsRejectedByGate()
    {
        var (row, handler) = await RunAsync(DropsPlaceholder, _ => $"CORRECTED: {DropsPlaceholder}", _ => Accepted);

        Assert.DoesNotContain(handler.Requests, r => r.Call.StartsWith("verification"));
        Assert.Equal(RejectedByGate, row.ActualCorrectionSafety);
        Assert.Equal(1, row.RepairAttemptsUsed);
    }

    [Fact(DisplayName = "A judge rejection sends its real defect token and evidence to the repair")]
    public async Task JudgeRejection_SendsTokenAndEvidence()
    {
        var (row, handler) = await RunAsync(Fixed, _ => "CORRECTED: {0} spars with {1} and {2}; {3} proves the superior fighter.",
            n => n == 1 ? "UNRESOLVED: NONE\nNEW_DEFECTS: MEANING_REVERSAL\nEVIDENCE: MEANING_REVERSAL: \"proves the superior\"\nSCORE: 40" : Accepted,
            evidence: true);

        var repair = Assert.Single(handler.Requests, r => r.Call == "repair");
        Assert.Contains("TARGET DEFECTS: MEANING_REVERSAL", repair.User);
        Assert.Contains("VERIFIER EVIDENCE: MEANING_REVERSAL: \"proves the superior\"", repair.User);
        Assert.Equal("Harmful", row.InitialCorrectionSafety);
        Assert.Equal("Safe", row.ActualCorrectionSafety);
    }

    [Fact(DisplayName = "A judged repair that returns the same draft stops without re-judging")]
    public async Task UnchangedRepair_StopsWithoutRejudging()
    {
        var (row, handler) = await RunAsync(Fixed, _ => $"CORRECTED: {Fixed}",
            _ => "UNRESOLVED: UNNATURAL_PHRASING\nNEW_DEFECTS: NONE\nSCORE: 0");

        Assert.Single(handler.Requests, r => r.Call == "verification");
        Assert.Equal("Harmful", row.ActualCorrectionSafety);
    }
}
