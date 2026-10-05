using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Workflow;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tests.Workflow;

/// <summary>
/// The verify/repair loop in <see cref="QualityReviewWorkflow.GetLlmVerdictAsync"/> must stop when a
/// repair hands back the candidate it was given: re-verifying identical text can only repeat the
/// same rejection, so the extra verification and repair calls are pure cost.
/// </summary>
public sealed class QcRepairLoopTests
{
    private const string Rejected = "UNRESOLVED: DROPPED_CONTENT\nNEW_DEFECTS: NONE\nSCORE: 0";
    private const string Accepted = "UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 95";

    /// <summary>Answers by call type (read from the system prompt) and counts each type.</summary>
    private sealed class ScriptedHandler(Func<int, string> repairReply, Func<int, string> verificationReply) : HttpMessageHandler
    {
        public Dictionary<string, int> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var system = JsonDocument.Parse(body).RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Calls[system] = Calls.GetValueOrDefault(system) + 1;
            var n = Calls[system];

            var reply = system switch
            {
                "detection" => "DEFECTS: DROPPED_CONTENT",
                "correction" => "CORRECTED: Fixed text",
                "verification" => verificationReply(n),
                "repair" => repairReply(n),
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
            ["BaseQualityReviewPrompt"] = "detection",
            ["BaseQualityReviewCorrectionPrompt"] = "correction",
            ["BaseQualityReviewVerificationPrompt"] = "verification",
            ["BaseQualityReviewCorrectionRepairPrompt"] = "repair",
        },
    };

    private static async Task<(QualityReviewWorkflow.LlmVerdict Verdict, ScriptedHandler Handler)> RunAsync(
        Func<int, string> repairReply, Func<int, string> verificationReply, int maxRepairs = 2)
    {
        var config = new LlmConfig
        {
            QualityReview = new QualityReviewConfig
            {
                Enabled = true,
                DoubledDetectionEnabled = false,
                MaxScoreRepairIterations = maxRepairs,
            },
        };
        var handler = new ScriptedHandler(repairReply, verificationReply);
        using var client = new HttpClient(handler);

        var verdict = await QualityReviewWorkflow.GetLlmVerdictAsync(config, BuildModel(), client, "你好", "你好", "Hello", string.Empty);
        return (verdict, handler);
    }

    [Fact(DisplayName = "A repair that returns the candidate unchanged stops the loop")]
    public async Task NoOpRepair_StopsWithoutReverifying()
    {
        var (verdict, handler) = await RunAsync(_ => "CORRECTED: Fixed text", _ => Rejected);

        Assert.Equal(1, handler.Calls["verification"]);
        Assert.Equal(1, handler.Calls["repair"]);
        Assert.True(verdict.Success);
        Assert.Equal("Fixed text", verdict.CorrectedRawMasked);
        Assert.Equal(0, verdict.Score);
    }

    [Fact(DisplayName = "A repair that changes the candidate is still re-verified")]
    public async Task ChangedRepair_IsReverified()
    {
        var (verdict, handler) = await RunAsync(_ => "CORRECTED: Better text", n => n == 1 ? Rejected : Accepted);

        Assert.Equal(2, handler.Calls["verification"]);
        Assert.Equal(1, handler.Calls["repair"]);
        Assert.Equal("Better text", verdict.CorrectedRawMasked);
        Assert.Equal(95, verdict.Score);
    }

    [Fact(DisplayName = "Repeated real repairs still stop at MaxScoreRepairIterations")]
    public async Task ChangingRepairs_StopAtMaxIterations()
    {
        var (verdict, handler) = await RunAsync(n => $"CORRECTED: Attempt {n}", _ => Rejected, maxRepairs: 2);

        Assert.Equal(3, handler.Calls["verification"]);
        Assert.Equal(2, handler.Calls["repair"]);
        Assert.Equal("Attempt 2", verdict.CorrectedRawMasked);
    }

    [Fact(DisplayName = "Zero MaxScoreRepairIterations verifies once and never repairs")]
    public async Task ZeroRepairs_VerifiesOnce()
    {
        var (_, handler) = await RunAsync(n => $"CORRECTED: Attempt {n}", _ => Rejected, maxRepairs: 0);

        Assert.Equal(1, handler.Calls["verification"]);
        Assert.False(handler.Calls.ContainsKey("repair"));
    }
}
