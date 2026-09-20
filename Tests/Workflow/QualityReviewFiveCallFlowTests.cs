using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

/// <summary>
/// End-to-end coverage of <see cref="QualityReviewWorkflow.GetLlmVerdictAsync"/>'s five-call
/// orchestration (detect x2, merge, generate, verify/repair loop), using <see cref="ScriptedLlmHandler"/>
/// (see TranslationServiceTests.cs) to script each call's response by matching on the labeled
/// section of the request body that call actually sends - never a real LLM.
/// </summary>
public sealed class QualityReviewFiveCallFlowTests
{
    private const string Source = "你好世界";
    private const string Translation = "Hello world";

    private static LlmConfig BuildConfig(List<ScriptedLlmHandler.Rule> rules, out HttpClient client, int maxRepairAttempts = 2)
    {
        var config = new LlmConfig
        {
            QualityReview = new QualityReviewConfig
            {
                Enabled = true,
                MaxScoreRepairIterations = maxRepairAttempts,
            },
        };

        config.Runtime.Models["Default"] = new ModelExecutionConfig
        {
            Url = "http://test.local/v1/chat/completions",
            ApiKeyRequired = false,
            Model = "test-model",
            Prompts = new Dictionary<string, string>
            {
                ["BaseQualityReviewPrompt"] = "detection system prompt",
                ["BaseQualityReviewCorrectionPrompt"] = "correction system prompt",
                ["BaseQualityReviewVerificationPrompt"] = "verification system prompt",
                ["BaseQualityReviewCorrectionRepairPrompt"] = "repair system prompt",
            },
        };

        client = new HttpClient(new ScriptedLlmHandler(rules));
        return config;
    }

    private static ScriptedLlmHandler.Rule DetectionRule(params string[] responses) => new()
    {
        Matches = c => c.Contains($"SOURCE (Chinese): {Source}")
            && !c.Contains("CONFIRMED DEFECTS")
            && !c.Contains("TARGET DEFECTS"),
        Responses = responses,
    };

    private static ScriptedLlmHandler.Rule CorrectionRule(string confirmedDefectsToken, string response) => new()
    {
        Matches = c => c.Contains($"CONFIRMED DEFECTS: {confirmedDefectsToken}") && !c.Contains("PROPOSED CORRECTION"),
        Responses = [response],
    };

    private static ScriptedLlmHandler.Rule VerificationRule(string proposedCorrection, string response) => new()
    {
        Matches = c => c.Contains($"PROPOSED CORRECTION: {proposedCorrection}"),
        Responses = [response],
    };

    private static ScriptedLlmHandler.Rule RepairRule(string targetDefectsToken, string previousAttempt, string response) => new()
    {
        Matches = c => c.Contains($"TARGET DEFECTS: {targetDefectsToken}") && c.Contains($"PREVIOUS ATTEMPT: {previousAttempt}"),
        Responses = [response],
    };

    private static async Task<QualityReviewWorkflow.LlmVerdict> RunAsync(LlmConfig config, HttpClient client) =>
        await QualityReviewWorkflow.GetLlmVerdictAsync(config, config.Runtime.Models["Default"], client, Source, Source, Translation, string.Empty);

    [Fact(DisplayName = "Both independent detectors agree NONE - accepted at fixed score 100, no correction")]
    public async Task BothDetectorsAgreeNone_AcceptsWithScore100()
    {
        var detection = DetectionRule("DEFECTS: NONE", "DEFECTS: NONE");
        var config = BuildConfig([detection], out var client);

        var verdict = await RunAsync(config, client);

        Assert.True(verdict.Success);
        Assert.Equal(100, verdict.Score);
        Assert.Null(verdict.CorrectedRawMasked);
        Assert.Equal(QcDefectCategory.None, verdict.Defect);
        Assert.Empty(verdict.Findings ?? []);
        Assert.Equal(2, detection.CallCount);
    }

    [Fact(DisplayName = "Call 2 catches a defect call 1 missed - merged into the confirmed set call 3/4 act on")]
    public async Task IndependentDetectorCatchesDefectFirstCallMissed()
    {
        // Call 1 says NONE, call 2 (fully independent - same prompt, fresh call) finds DOMAIN_TERM.
        // This is exactly the "first detector misses, independent detector finds it" case the
        // comparison plan asks assessments to cover.
        var detection = DetectionRule("DEFECTS: NONE", "DEFECTS: DOMAIN_TERM");
        var correction = CorrectionRule("DOMAIN_TERM", "CORRECTED: Hello realm");
        var verification = VerificationRule("Hello realm", "UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 95");
        var config = BuildConfig([detection, correction, verification], out var client);

        var verdict = await RunAsync(config, client);

        Assert.True(verdict.Success);
        Assert.Equal(95, verdict.Score);
        Assert.Equal("Hello realm", verdict.CorrectedRawMasked);
        Assert.Equal(QcDefectCategory.DomainTerm, verdict.Defect);
        Assert.Equal(1, correction.CallCount);
        Assert.Equal(1, verification.CallCount);
    }

    [Fact(DisplayName = "UNCERTAIN is never treated as None - flags for a human, no correction attempted")]
    public async Task UncertainAlone_FlagsForHumanNoCorrection()
    {
        var detection = DetectionRule("DEFECTS: UNCERTAIN", "DEFECTS: NONE");
        var config = BuildConfig([detection], out var client);

        var verdict = await RunAsync(config, client);

        Assert.True(verdict.Success);
        Assert.Null(verdict.Score);
        Assert.Null(verdict.CorrectedRawMasked);
        Assert.Equal(QcDefectCategory.Uncertain, verdict.Defect);
    }

    [Fact(DisplayName = "Call 4 finds an unresolved defect - call 5 repairs, call 4 re-verifies the full confirmed set")]
    public async Task VerificationFindsUnresolvedDefect_RepairsAndReverifies()
    {
        var detection = DetectionRule("DEFECTS: DROPPED_CONTENT", "DEFECTS: DROPPED_CONTENT");
        var correction = CorrectionRule("DROPPED_CONTENT", "CORRECTED: attempt1");
        var verify1 = VerificationRule("attempt1", "UNRESOLVED: DROPPED_CONTENT\nNEW_DEFECTS: NONE\nSCORE: 30");
        var repair = RepairRule("DROPPED_CONTENT", "attempt1", "CORRECTED: attempt2");
        var verify2 = VerificationRule("attempt2", "UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 90");
        var config = BuildConfig([detection, correction, verify1, repair, verify2], out var client);

        var verdict = await RunAsync(config, client);

        Assert.True(verdict.Success);
        Assert.Equal(90, verdict.Score);
        Assert.Equal("attempt2", verdict.CorrectedRawMasked);
        Assert.Equal(1, verify1.CallCount);
        Assert.Equal(1, repair.CallCount);
        Assert.Equal(1, verify2.CallCount);
    }

    [Fact(DisplayName = "A new defect introduced by the correction blocks acceptance even if the original is resolved")]
    public async Task NewDefectIntroduced_BlocksAcceptanceUntilRepaired()
    {
        var detection = DetectionRule("DEFECTS: DOMAIN_TERM", "DEFECTS: DOMAIN_TERM");
        var correction = CorrectionRule("DOMAIN_TERM", "CORRECTED: attempt1");
        // Original DOMAIN_TERM is resolved, but the correction introduced a new GARBLED_NUMBER -
        // must not be accepted despite UNRESOLVED being NONE.
        var verify1 = VerificationRule("attempt1", "UNRESOLVED: NONE\nNEW_DEFECTS: GARBLED_NUMBER\nSCORE: 90");
        var repair = RepairRule("GARBLED_NUMBER", "attempt1", "CORRECTED: attempt2");
        var verify2 = VerificationRule("attempt2", "UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 95");
        var config = BuildConfig([detection, correction, verify1, repair, verify2], out var client);

        var verdict = await RunAsync(config, client);

        Assert.True(verdict.Success);
        Assert.Equal(95, verdict.Score);
        Assert.Equal("attempt2", verdict.CorrectedRawMasked);
        // Final defect reported is the ORIGINAL confirmed set (DomainTerm), not the transient
        // GarbledNumber that only ever existed mid-repair.
        Assert.Equal(QcDefectCategory.DomainTerm, verdict.Defect);
        Assert.Equal(1, repair.CallCount);
    }

    [Fact(DisplayName = "Repair budget exhausted - accepts the last verified candidate/score without another repair call")]
    public async Task RepairBudgetExhausted_AcceptsLastVerifiedCandidate()
    {
        var detection = DetectionRule("DEFECTS: DROPPED_CONTENT", "DEFECTS: DROPPED_CONTENT");
        var correction = CorrectionRule("DROPPED_CONTENT", "CORRECTED: attempt1");
        var verify1 = VerificationRule("attempt1", "UNRESOLVED: DROPPED_CONTENT\nNEW_DEFECTS: NONE\nSCORE: 20");
        var config = BuildConfig([detection, correction, verify1], out var client, maxRepairAttempts: 0);

        var verdict = await RunAsync(config, client);

        Assert.True(verdict.Success);
        Assert.Equal(20, verdict.Score);
        Assert.Equal("attempt1", verdict.CorrectedRawMasked);
        Assert.Equal(QcDefectCategory.DroppedContent, verdict.Defect);
    }
}
