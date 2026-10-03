using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Workflow;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tests.Workflow;

/// <summary>
/// <see cref="QualityReviewConfig.DetectionPrefillEnabled"/> must append a trailing "DEFECTS:" assistant
/// message to the detection request only, and the parser must accept the answer with or without
/// the echoed prefix.
/// </summary>
public sealed class QcDetectionPrefillTests
{
    private sealed class CapturingHandler(string reply) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
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
        Prompts = new Dictionary<string, string> { ["BaseQualityReviewPrompt"] = "detection system prompt" },
    };

    private static async Task<(JsonElement Messages, FanslationStudio.LlmKit.Support.QcDetectionResult Result)> DetectAsync(bool prefill, string reply)
    {
        var config = new LlmConfig { QualityReview = new QualityReviewConfig { Enabled = true, DetectionPrefillEnabled = prefill } };
        var handler = new CapturingHandler(reply);
        using var client = new HttpClient(handler);
        var result = await QualityReviewWorkflow.DetectDefectsAsync(config, BuildModel(), client, "你好", "你好", "Hello", string.Empty, null);
        return (JsonDocument.Parse(handler.LastBody!).RootElement.GetProperty("messages").Clone(), result);
    }

    [Fact(DisplayName = "Detection prefill adds a trailing DEFECTS: assistant message")]
    public async Task Enabled_AppendsAssistantPrefill()
    {
        var (messages, result) = await DetectAsync(true, "DEFECTS: NONE");

        Assert.Equal(3, messages.GetArrayLength());
        var last = messages[2];
        Assert.Equal("assistant", last.GetProperty("role").GetString());
        Assert.Equal("DEFECTS:", last.GetProperty("content").GetString());
        Assert.True(result.Success);
        Assert.Empty(result.Findings);
    }

    [Fact(DisplayName = "Detection prefill off keeps the plain system+user request")]
    public async Task Disabled_SendsNoPrefill()
    {
        var (messages, _) = await DetectAsync(false, "DEFECTS: NONE");

        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact(DisplayName = "Detection prefill is on by default")]
    public void DetectionPrefill_DefaultsToEnabled() =>
        Assert.True(new QualityReviewConfig().DetectionPrefillEnabled);

    [Theory(DisplayName = "Prefilled detection parses a continuation without the DEFECTS: prefix")]
    [InlineData(" NONE")]
    [InlineData("NONE")]
    [InlineData("DEFECTS: NONE")]
    public async Task Enabled_ParsesNoneWithOrWithoutPrefix(string reply)
    {
        var (_, result) = await DetectAsync(true, reply);

        Assert.True(result.Success);
        Assert.Empty(result.Findings);
    }

    [Fact(DisplayName = "Prefilled detection parses a bare category list")]
    public async Task Enabled_ParsesBareCategoryList()
    {
        var (_, result) = await DetectAsync(true, " DROPPED_CONTENT, UNNATURAL_PHRASING");

        Assert.True(result.Success);
        Assert.Equal(2, result.Findings.Count);
    }

    [Fact(DisplayName = "Without prefill a bare answer is still a parse failure")]
    public void Parser_BareAnswerFailsWithoutAssumePrefix()
    {
        Assert.False(QcDetectionResponseParser.Parse("NONE").Success);
        Assert.True(QcDetectionResponseParser.Parse("NONE", assumeDefectsPrefix: true).Success);
    }
}
