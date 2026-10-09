using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tests.Workflow;

/// <summary>
/// <see cref="QualityControlConfig.VerificationEvidenceEnabled"/>: the verifier quotes the text each
/// rejection is about, a claim whose quote is not in SOURCE/the candidate is overruled, and surviving
/// quotes reach the repair call.
/// </summary>
public sealed class QcVerificationEvidenceTests
{
    private const string Source = "{0}在{1}与{2}切磋武艺，最终{3}技高一筹。";
    private const string Candidate = "{0} spars with {1} and {2}, ultimately proving to be the superior fighter.";

    private static QcVerificationResult Parse(string reply) =>
        QcVerificationResponseParser.Parse(reply, [QcDefectCategory.UnnaturalPhrasing]);

    [Fact(DisplayName = "EVIDENCE entries parse per category; a missing line leaves Evidence null")]
    public void Parse_ReadsEvidenceEntries()
    {
        var result = Parse("UNRESOLVED: NONE\nNEW_DEFECTS: DROPPED_CONTENT, MEANING_REVERSAL\nEVIDENCE: DROPPED_CONTENT: \"{3}\" | MEANING_REVERSAL: “proving to be”\nSCORE: 20");

        Assert.True(result.Success);
        Assert.Equal("{3}", result.Evidence![QcDefectCategory.DroppedContent]);
        Assert.Equal("proving to be", result.Evidence[QcDefectCategory.MeaningReversal]);
        Assert.Null(Parse("UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 95").Evidence);
    }

    [Theory(DisplayName = "FilterByEvidence keeps only claims whose quote is really there")]
    [InlineData("DROPPED_CONTENT", "{3}", true)]                 // source text the candidate lacks
    [InlineData("DROPPED_CONTENT", "Senior Disciple", false)]    // only in the old translation
    [InlineData("DROPPED_CONTENT", "proving to be", false)]      // dropped content must quote SOURCE
    [InlineData("MEANING_REVERSAL", "proving to be", true)]      // wrong words in the candidate
    [InlineData("MEANING_REVERSAL", "made up words", false)]
    public void FilterByEvidence_ChecksQuoteLocation(string category, string quote, bool kept)
    {
        var result = Parse($"UNRESOLVED: NONE\nNEW_DEFECTS: {category}\nEVIDENCE: {category}: \"{quote}\"\nSCORE: 30");

        var filtered = QcVerificationResponseParser.FilterByEvidence(result, Source, Candidate);

        Assert.Equal(kept, filtered.NewDefects.Count == 1);
        Assert.Equal(!kept, filtered.Accepted);
    }

    [Fact(DisplayName = "A rejection that survives the filter scores 0, never its real grade")]
    public void FilterByEvidence_SurvivingRejectionScoresZero()
    {
        var result = Parse("UNRESOLVED: NONE\nNEW_DEFECTS: DROPPED_CONTENT\nEVIDENCE: DROPPED_CONTENT: \"{3}\"\nSCORE: 85");

        var filtered = QcVerificationResponseParser.FilterByEvidence(result, Source, Candidate);

        Assert.False(filtered.Accepted);
        Assert.Equal(0, filtered.Score);
    }

    [Fact(DisplayName = "A punctuation-only quote is not evidence")]
    public void FilterByEvidence_PunctuationQuoteIsNotEvidence()
    {
        var result = Parse("UNRESOLVED: NONE\nNEW_DEFECTS: DROPPED_CONTENT\nEVIDENCE: DROPPED_CONTENT: \"。\"\nSCORE: 85");

        var filtered = QcVerificationResponseParser.FilterByEvidence(result, Source, Candidate);

        Assert.True(filtered.Accepted);
        Assert.Equal(85, filtered.Score);
    }

    [Fact(DisplayName = "With EVIDENCE, an unconfirmed UNRESOLVED category is a new defect, not a parse failure")]
    public void Parse_UnconfirmedUnresolvedWithEvidence_BecomesNewDefect()
    {
        // Real reply from the gold set's MEANING_REVERSAL row (confirmed: OTHER_NAMED_DEFECT), 2026-10-04.
        var result = QcVerificationResponseParser.Parse(
            "UNRESOLVED: MEANING_REVERSAL\nNEW_DEFECTS: NONE\nEVIDENCE: MEANING_REVERSAL: \"放我顾师弟走\"\nSCORE: 45",
            [QcDefectCategory.OtherNamedDefect]);

        Assert.True(result.Success);
        Assert.Empty(result.UnresolvedDefects);
        Assert.Equal([QcDefectCategory.MeaningReversal], result.NewDefects);
        Assert.False(QcVerificationResponseParser.FilterByEvidence(result, "大！当！家！放我顾师弟走，饶你不死。", "...").Accepted);
    }

    [Fact(DisplayName = "A claim with no evidence entry is kept")]
    public void FilterByEvidence_KeepsClaimWithoutEntry()
    {
        var result = Parse("UNRESOLVED: UNNATURAL_PHRASING\nNEW_DEFECTS: NONE\nEVIDENCE: NONE\nSCORE: 30");

        Assert.Equal([QcDefectCategory.UnnaturalPhrasing], QcVerificationResponseParser.FilterByEvidence(result, Source, Candidate).UnresolvedDefects);
    }

    private sealed class ScriptedHandler(string verificationReply) : HttpMessageHandler
    {
        public List<(string Call, string User)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var messages = JsonDocument.Parse(body).RootElement.GetProperty("messages");
            var system = messages[0].GetProperty("content").GetString()!;
            Requests.Add((system, messages[1].GetProperty("content").GetString()!));
            var reply = system switch
            {
                "detection" => "DEFECTS: UNNATURAL_PHRASING",
                "correction" => $"CORRECTED: {Candidate}",
                "verification" or "verification-evidence" => verificationReply,
                "repair" => "CORRECTED: {0} spars with {1} and {2}; in the end {3} proves the superior fighter.",
                _ => throw new InvalidOperationException(system),
            };
            var json = JsonSerializer.Serialize(new { message = new { content = reply }, done_reason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(QualityControlWorkflow.LlmVerdict Verdict, ScriptedHandler Handler)> RunAsync(string verificationReply, bool evidenceEnabled = true)
    {
        var config = new LlmConfig
        {
            QualityControl = new QualityControlConfig
            {
                Enabled = true,
                DoubledDetectionEnabled = false,
                MaxScoreRepairIterations = 1,
                VerificationEvidenceEnabled = evidenceEnabled,
            },
        };
        var model = new ModelExecutionConfig
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
                ["BaseQualityControlVerificationEvidencePrompt"] = "verification-evidence",
                ["BaseQualityControlCorrectionRepairPrompt"] = "repair",
            },
        };
        var handler = new ScriptedHandler(verificationReply);
        using var client = new HttpClient(handler);

        var verdict = await QualityControlWorkflow.GetLlmVerdictAsync(config, model, client, Source, Source, "{0} In {1} And {2} Sparring, in the end {3} One notch above", string.Empty);
        return (verdict, handler);
    }

    [Fact(DisplayName = "A rejection quoting text that is not in SOURCE is overruled without a repair")]
    public async Task BogusEvidence_IsOverruled()
    {
        var (verdict, handler) = await RunAsync("UNRESOLVED: NONE\nNEW_DEFECTS: DROPPED_CONTENT\nEVIDENCE: DROPPED_CONTENT: \"One notch above\"\nSCORE: 85");

        Assert.Contains(handler.Requests, request => request.Call == "verification-evidence");
        Assert.DoesNotContain(handler.Requests, request => request.Call == "repair");
        Assert.Equal(Candidate, verdict.CorrectedRawMasked);
        Assert.Equal(85, verdict.Score);
    }

    [Fact(DisplayName = "A rejection with real evidence sends the quote to the repair")]
    public async Task RealEvidence_ReachesRepair()
    {
        var (_, handler) = await RunAsync("UNRESOLVED: NONE\nNEW_DEFECTS: DROPPED_CONTENT\nEVIDENCE: DROPPED_CONTENT: \"{3}\"\nSCORE: 10");

        var repair = Assert.Single(handler.Requests, request => request.Call == "repair");
        Assert.Contains("VERIFIER EVIDENCE: DROPPED_CONTENT: \"{3}\"", repair.User);
    }

    [Fact(DisplayName = "With evidence off the plain verification prompt is used")]
    public async Task EvidenceDisabled_UsesPlainPrompt()
    {
        var (_, handler) = await RunAsync("UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 95", evidenceEnabled: false);

        Assert.Contains(handler.Requests, request => request.Call == "verification");
        Assert.DoesNotContain(handler.Requests, request => request.Call == "verification-evidence");
    }

    [Fact(DisplayName = "VerificationEvidenceEnabled defaults to off")]
    public void VerificationEvidence_DefaultsToDisabled() =>
        Assert.False(new QualityControlConfig().VerificationEvidenceEnabled);
}
