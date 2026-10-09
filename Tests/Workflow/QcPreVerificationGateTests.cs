using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Workflow;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tests.Workflow;

/// <summary>
/// <see cref="QualityControlConfig.PreVerificationGateEnabled"/>: a candidate the validation gate rejects
/// is never verified; it goes straight to a repair told the gate's reason, and only a candidate that
/// passes the gate reaches verification.
/// </summary>
public sealed class QcPreVerificationGateTests
{
    private const string Accepted = "UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 95";
    private const string Rejected = "UNRESOLVED: DROPPED_CONTENT\nNEW_DEFECTS: NONE\nSCORE: 0";
    private const string GateReason = "Restore `{1}` to the translation, as it was incorrectly removed.";

    /// <summary>Answers by call type (read from the system prompt), recording each call's user prompt in order.</summary>
    private sealed class ScriptedHandler(Func<int, string> correctionReply, Func<int, string> repairReply, Func<int, string> verificationReply) : HttpMessageHandler
    {
        public List<(string Call, string User)> Requests { get; } = new();

        public int Count(string call) => Requests.Count(request => request.Call == call);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var messages = JsonDocument.Parse(body).RootElement.GetProperty("messages");
            var system = messages[0].GetProperty("content").GetString()!;
            Requests.Add((system, messages[1].GetProperty("content").GetString()!));
            var n = Count(system);

            var reply = system switch
            {
                "detection" => "DEFECTS: DROPPED_CONTENT",
                "correction" => correctionReply(n),
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
            ["BaseQualityControlPrompt"] = "detection",
            ["BaseQualityControlCorrectionPrompt"] = "correction",
            ["BaseQualityControlVerificationPrompt"] = "verification",
            ["BaseQualityControlCorrectionRepairPrompt"] = "repair",
        },
    };

    /// <summary>Gate: any candidate without "{1}" fails with <see cref="GateReason"/>.</summary>
    private static string? PlaceholderGate(string candidate) => candidate.Contains("{1}") ? null : GateReason;

    private static async Task<(QualityControlWorkflow.LlmVerdict Verdict, ScriptedHandler Handler)> RunAsync(
        Func<string, string?>? validator,
        Func<int, string> repairReply,
        Func<int, string>? verificationReply = null,
        string correction = "CORRECTED: {0} wins",
        int maxRepairs = 1)
    {
        var config = new LlmConfig
        {
            QualityControl = new QualityControlConfig
            {
                Enabled = true,
                DoubledDetectionEnabled = false,
                MaxScoreRepairIterations = maxRepairs,
            },
        };
        var handler = new ScriptedHandler(_ => correction, repairReply, verificationReply ?? (_ => Accepted));
        using var client = new HttpClient(handler);

        var verdict = await QualityControlWorkflow.GetLlmVerdictAsync(
            config, BuildModel(), client, "{0}与{1}", "{0}与{1}", "{0} and {1}", string.Empty, candidateValidator: validator);
        return (verdict, handler);
    }

    [Fact(DisplayName = "A gate-failing correction is repaired with the gate's reason before any verification")]
    public async Task GateFailure_RepairsWithReason_ThenVerifiesFixedCandidate()
    {
        var (verdict, handler) = await RunAsync(PlaceholderGate, _ => "CORRECTED: {0} beats {1}");

        Assert.Equal(["detection", "correction", "repair", "verification"], handler.Requests.Select(request => request.Call));
        Assert.Contains($"RULE CHECK FAILURE: {GateReason}", handler.Requests[2].User);
        Assert.Contains("PROPOSED CORRECTION: {0} beats {1}", handler.Requests[3].User);
        Assert.Equal("{0} beats {1}", verdict.CorrectedRawMasked);
        Assert.Equal(95, verdict.Score);
    }

    [Fact(DisplayName = "A gate repair that returns the candidate unchanged stops without verifying")]
    public async Task UnchangedGateRepair_StopsWithoutVerification()
    {
        var (verdict, handler) = await RunAsync(PlaceholderGate, _ => "CORRECTED: {0} wins");

        Assert.Equal(1, handler.Count("repair"));
        Assert.Equal(0, handler.Count("verification"));
        Assert.Equal("{0} wins", verdict.CorrectedRawMasked);
    }

    [Fact(DisplayName = "A gate failure with no repair budget left returns the candidate without verifying")]
    public async Task GateFailure_NoBudget_ReturnsWithoutCalls()
    {
        var (verdict, handler) = await RunAsync(PlaceholderGate, _ => "CORRECTED: {0} beats {1}", maxRepairs: 0);

        Assert.Equal(0, handler.Count("repair"));
        Assert.Equal(0, handler.Count("verification"));
        Assert.Equal("{0} wins", verdict.CorrectedRawMasked);
    }

    [Fact(DisplayName = "A blank gate reason is not sent to a repair")]
    public async Task BlankGateReason_DoesNotRepair()
    {
        var (_, handler) = await RunAsync(_ => " ", _ => "CORRECTED: {0} beats {1}");

        Assert.Equal(0, handler.Count("repair"));
        Assert.Equal(0, handler.Count("verification"));
    }

    [Fact(DisplayName = "A gate repair shares the repair budget with verification repairs")]
    public async Task GateRepair_ConsumesRepairBudget()
    {
        var (verdict, handler) = await RunAsync(PlaceholderGate, _ => "CORRECTED: {0} beats {1}", _ => Rejected, maxRepairs: 1);

        Assert.Equal(1, handler.Count("repair"));
        Assert.Equal(1, handler.Count("verification"));
        Assert.Equal(0, verdict.Score);
    }

    [Fact(DisplayName = "Without a validator the loop verifies first and repairs send no rule line")]
    public async Task NoValidator_KeepsVerifyFirstFlow()
    {
        var (_, handler) = await RunAsync(null, _ => "CORRECTED: {0} beats {1}", n => n == 1 ? Rejected : Accepted);

        Assert.Equal(["detection", "correction", "verification", "repair", "verification"], handler.Requests.Select(request => request.Call));
        Assert.DoesNotContain("RULE CHECK FAILURE", handler.Requests[3].User);
    }

    [Fact(DisplayName = "PreVerificationGateEnabled defaults to on")]
    public void PreVerificationGate_DefaultsToEnabled() =>
        Assert.True(new QualityControlConfig().PreVerificationGateEnabled);
}
