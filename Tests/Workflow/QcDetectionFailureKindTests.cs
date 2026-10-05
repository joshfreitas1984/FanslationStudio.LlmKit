using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;
using System.Net;
using System.Text;

namespace Tests.Workflow;

/// <summary>
/// <see cref="QualityReviewWorkflow.DetectDefectsAsync"/> must report WHY a detection failed, not
/// just <c>Success = false</c> - a context-size 400 and a "length"-truncated answer were once
/// indistinguishable from a genuine protocol violation in the QC evaluator's Results.yaml (see
/// docs/features/translation-pipeline/quality-review-pass.md's "Context headroom regression").
/// </summary>
public sealed class QcDetectionFailureKindTests
{
    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static async Task<QcDetectionResult> DetectAsync(HttpStatusCode status, string body)
    {
        var config = new LlmConfig { QualityReview = new QualityReviewConfig { Enabled = true } };
        var model = new ModelExecutionConfig
        {
            Url = "http://test.local/api/chat",
            ApiKeyRequired = false,
            Model = "test-model",
            Prompts = new Dictionary<string, string> { ["BaseQualityReviewPrompt"] = "detection system prompt" },
        };
        using var client = new HttpClient(new FixedResponseHandler(status, body));
        return await QualityReviewWorkflow.DetectDefectsAsync(config, model, client, "你好", "你好", "Hello", string.Empty, null);
    }

    [Fact(DisplayName = "Context-size 400 is reported as RequestError with the server's message")]
    public async Task ContextSize400_IsRequestError()
    {
        var result = await DetectAsync(HttpStatusCode.BadRequest,
            """{"error":"request (4108 tokens) exceeds the available context size (4096 tokens)"}""");

        Assert.False(result.Success);
        Assert.Equal(QcDetectionFailureKind.RequestError, result.FailureKind);
        Assert.Contains("exceeds the available context size", result.FailureDetail);
    }

    [Fact(DisplayName = "Ollama done_reason 'length' with a cut-off answer is reported as Truncated")]
    public async Task OllamaLengthStop_IsTruncated()
    {
        var result = await DetectAsync(HttpStatusCode.OK,
            """{"message":{"role":"assistant","content":"DEFECTS: HARD_TO_PARSE_SE"},"done":true,"done_reason":"length"}""");

        Assert.False(result.Success);
        Assert.Equal(QcDetectionFailureKind.Truncated, result.FailureKind);
        Assert.Equal("DEFECTS: HARD_TO_PARSE_SE", result.FailureDetail);
    }

    [Fact(DisplayName = "OpenAI finish_reason 'length' is also reported as Truncated")]
    public async Task OpenAiLengthStop_IsTruncated()
    {
        var result = await DetectAsync(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"DEFECTS: HARD_TO"},"finish_reason":"length"}]}""");

        Assert.Equal(QcDetectionFailureKind.Truncated, result.FailureKind);
    }

    [Fact(DisplayName = "A normally-finished malformed answer is reported as ParseError")]
    public async Task NormalStopMalformed_IsParseError()
    {
        var result = await DetectAsync(HttpStatusCode.OK,
            """{"message":{"role":"assistant","content":"DEFECTS: NOT_A_CATEGORY"},"done":true,"done_reason":"stop"}""");

        Assert.False(result.Success);
        Assert.Equal(QcDetectionFailureKind.ParseError, result.FailureKind);
        Assert.Equal("DEFECTS: NOT_A_CATEGORY", result.FailureDetail);
    }

    [Fact(DisplayName = "A successful detection carries no failure kind")]
    public async Task Success_HasNoFailureKind()
    {
        var result = await DetectAsync(HttpStatusCode.OK,
            """{"message":{"role":"assistant","content":"DEFECTS: NONE"},"done":true,"done_reason":"stop"}""");

        Assert.True(result.Success);
        Assert.Equal(QcDetectionFailureKind.None, result.FailureKind);
        Assert.Null(result.FailureDetail);
    }

    [Fact(DisplayName = "Merge propagates the first failed call's failure kind and detail")]
    public void Merge_PropagatesFailureKind()
    {
        var ok = new QcDetectionResult(true, []);
        var failed = new QcDetectionResult(false, [], QcDetectionFailureKind.Truncated, "DEFECTS: HARD");

        var merged = QcDetectionResult.Merge(ok, failed);

        Assert.False(merged.Success);
        Assert.Equal(QcDetectionFailureKind.Truncated, merged.FailureKind);
        Assert.Equal("DEFECTS: HARD", merged.FailureDetail);
    }
}
