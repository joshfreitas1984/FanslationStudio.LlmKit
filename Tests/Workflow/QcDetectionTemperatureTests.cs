using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Workflow;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tests.Workflow;

/// <summary>
/// <see cref="QualityControlConfig.DetectionTemperature"/> must reach the detection request's
/// options.temperature without mutating the shared model config the correction calls also use.
/// </summary>
public sealed class QcDetectionTemperatureTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":{"content":"DEFECTS: NONE"},"done_reason":"stop"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static ModelExecutionConfig BuildModel() => new()
    {
        Url = "http://test.local/api/chat",
        ApiKeyRequired = false,
        Model = "test-model",
        ModelParams = new Dictionary<string, object> { ["temperature"] = 0.15, ["num_ctx"] = 8192 },
        Prompts = new Dictionary<string, string> { ["BaseQualityControlPrompt"] = "detection system prompt" },
    };

    private static async Task<JsonElement> DetectAndCaptureOptionsAsync(double? detectionTemperature, ModelExecutionConfig model)
    {
        var config = new LlmConfig { QualityControl = new QualityControlConfig { Enabled = true, DetectionTemperature = detectionTemperature } };
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        await QualityControlWorkflow.DetectDefectsAsync(config, model, client, "你好", "你好", "Hello", string.Empty, null);
        return JsonDocument.Parse(handler.LastBody!).RootElement.GetProperty("options").Clone();
    }

    [Fact(DisplayName = "DetectionTemperature overrides options.temperature on the detection request only")]
    public async Task DetectionTemperature_OverridesRequestWithoutMutatingModel()
    {
        var model = BuildModel();

        var options = await DetectAndCaptureOptionsAsync(0, model);

        Assert.Equal(0m, options.GetProperty("temperature").GetDecimal());
        Assert.Equal(8192m, options.GetProperty("num_ctx").GetDecimal());
        Assert.Equal(0.15, model.ModelParams!["temperature"]);
    }

    [Fact(DisplayName = "Null DetectionTemperature keeps the model's own temperature")]
    public async Task NullDetectionTemperature_KeepsModelTemperature()
    {
        var options = await DetectAndCaptureOptionsAsync(null, BuildModel());

        Assert.Equal(0.15m, options.GetProperty("temperature").GetDecimal());
    }
}
