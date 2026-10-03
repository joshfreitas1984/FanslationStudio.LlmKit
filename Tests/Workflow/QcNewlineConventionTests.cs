using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Workflow;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tests.Workflow;

/// <summary>
/// A QC correction must use SOURCE's line-break form: the correction prompts ask for a literal "\n",
/// which turned a real line break into visible "\n" text for sources that use real breaks.
/// </summary>
public sealed class QcNewlineConventionTests
{
    [Theory(DisplayName = "MatchSourceNewlines follows the source's line-break form")]
    [InlineData("甲\n乙", "A\\nB", "A\nB")]
    [InlineData("甲\\n乙", "A\nB", "A\\nB")]
    [InlineData("甲\\n乙", "A\r\nB", "A\\nB")]
    [InlineData("甲\n乙", "A\nB", "A\nB")]
    [InlineData("甲\\n乙", "A\\nB", "A\\nB")]
    [InlineData("甲乙", "A\\nB", "A\\nB")]
    [InlineData("甲\n乙\\n丙", "A\\nB\nC", "A\\nB\nC")]
    public void MatchSourceNewlines(string source, string candidate, string expected) =>
        Assert.Equal(expected, QualityReviewWorkflow.MatchSourceNewlines(candidate, source));

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var system = JsonDocument.Parse(body).RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            var reply = system switch
            {
                "detection" => "DEFECTS: DROPPED_STUTTER",
                "correction" => "CORRECTED: H-hello\\nBye",
                "verification" => "UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 95",
                _ => throw new InvalidOperationException(system),
            };
            var json = JsonSerializer.Serialize(new { message = new { content = reply }, done_reason = "stop" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact(DisplayName = "A literal \\n correction for a real-line-break source is returned with a real break")]
    public async Task LiteralNewlineCorrection_UsesSourceRealBreak()
    {
        var config = new LlmConfig { QualityReview = new QualityReviewConfig { Enabled = true, DoubledDetectionEnabled = false } };
        var model = new ModelExecutionConfig
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
        using var client = new HttpClient(new ScriptedHandler());

        var verdict = await QualityReviewWorkflow.GetLlmVerdictAsync(config, model, client, "你、你好\n再见", "你、你好\n再见", "Hello\nBye", string.Empty);

        Assert.Equal("H-hello\nBye", verdict.CorrectedRawMasked);
    }
}
