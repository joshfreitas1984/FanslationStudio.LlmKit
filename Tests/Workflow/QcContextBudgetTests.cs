using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Workflow;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tests.Workflow;

/// <summary>
/// <see cref="QualityReviewWorkflow.CheckDetectionContextBudgetAsync"/> must fail a review pass up
/// front when a detection prompt would not fit num_ctx, and otherwise stay out of the way.
/// </summary>
public sealed class QcContextBudgetTests
{
    private sealed class PromptCountHandler(Func<string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            var (status, responseBody) = respond(body);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }

    private static ModelExecutionConfig BuildModel(string url = "http://test.local/api/chat", object? numCtx = null) => new()
    {
        Url = url,
        ApiKeyRequired = false,
        Model = "test-model",
        ModelParams = new Dictionary<string, object> { ["temperature"] = 0.15, ["num_ctx"] = numCtx ?? 6144, ["num_predict"] = 4096 },
        Prompts = new Dictionary<string, string> { ["BaseQualityReviewPrompt"] = "detection system prompt" },
    };

    private static LlmConfig BuildConfig() => new() { QualityReview = new QualityReviewConfig { Enabled = true } };

    private static PromptCountHandler Counting(int promptEvalCount) =>
        new(_ => (HttpStatusCode.OK, $$"""{"message":{"content":"D"},"done_reason":"length","prompt_eval_count":{{promptEvalCount}}}"""));

    [Fact(DisplayName = "Context check passes when every probed prompt fits with the answer reserve")]
    public async Task FittingPrompt_Passes()
    {
        var handler = Counting(6144 - QualityReviewWorkflow.DetectionAnswerReserveTokens);
        using var client = new HttpClient(handler);

        await QualityReviewWorkflow.CheckDetectionContextBudgetAsync(BuildConfig(), BuildModel(), client, ["SOURCE (Chinese): a", "SOURCE (Chinese): b"]);

        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact(DisplayName = "Context check throws when a prompt leaves less than the answer reserve")]
    public async Task OversizedPrompt_Throws()
    {
        using var client = new HttpClient(Counting(6144 - QualityReviewWorkflow.DetectionAnswerReserveTokens + 1));

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            QualityReviewWorkflow.CheckDetectionContextBudgetAsync(BuildConfig(), BuildModel(), client, ["SOURCE (Chinese): a"]));

        Assert.Contains("num_ctx 6144", e.Message);
    }

    [Fact(DisplayName = "Context check throws when the server rejects the probe as over the context size")]
    public async Task ServerContextRejection_Throws()
    {
        using var client = new HttpClient(new PromptCountHandler(_ =>
            (HttpStatusCode.BadRequest, """{"error":{"type":"exceed_context_size_error","message":"request exceeds the available context size"}}""")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            QualityReviewWorkflow.CheckDetectionContextBudgetAsync(BuildConfig(), BuildModel(), client, ["SOURCE (Chinese): a"]));
    }

    [Fact(DisplayName = "Context check only warns when the probe gets no token count")]
    public async Task UnusableProbeResponse_DoesNotThrow()
    {
        using var client = new HttpClient(new PromptCountHandler(_ => (HttpStatusCode.InternalServerError, """{"error":"model runner has unexpectedly stopped"}""")));

        await QualityReviewWorkflow.CheckDetectionContextBudgetAsync(BuildConfig(), BuildModel(), client, ["SOURCE (Chinese): a"]);
    }

    [Fact(DisplayName = "Context check is skipped for non-Ollama endpoints")]
    public async Task NonOllamaEndpoint_SendsNothing()
    {
        var handler = Counting(999_999);
        using var client = new HttpClient(handler);

        await QualityReviewWorkflow.CheckDetectionContextBudgetAsync(BuildConfig(), BuildModel(url: "http://test.local/v1/chat/completions"), client, ["SOURCE (Chinese): a"]);

        Assert.Empty(handler.Bodies);
    }

    [Fact(DisplayName = "Context probe sends the real detection prompt with num_predict 1 and the configured num_ctx")]
    public async Task Probe_UsesDetectionPromptAndSingleTokenGeneration()
    {
        var handler = Counting(100);
        using var client = new HttpClient(handler);
        var model = BuildModel(numCtx: "6144");

        await QualityReviewWorkflow.CheckDetectionContextBudgetAsync(BuildConfig(), model, client, ["SOURCE (Chinese): a"]);

        var request = JsonDocument.Parse(handler.Bodies.Single()).RootElement;
        Assert.Equal(1m, request.GetProperty("options").GetProperty("num_predict").GetDecimal());
        Assert.Equal(6144m, request.GetProperty("options").GetProperty("num_ctx").GetDecimal());
        Assert.Equal("detection system prompt", request.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("SOURCE (Chinese): a", request.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(4096, model.ModelParams!["num_predict"]);
    }
}
