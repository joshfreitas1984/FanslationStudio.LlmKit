using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tests.Workflow;

/// <summary>
/// Pins the exact user prompt every QC call sends - the shared builder replaced four hand-written
/// copies, and the model-facing text must stay byte-identical (only the glossary block is omitted
/// when there's no applicable term).
/// </summary>
public sealed class QcPromptBuilderTests
{
    private static readonly string NL = Environment.NewLine;
    private const string Glossary = "- 剑法 = Sword Technique";

    private static string Expected(params string[] lines) => string.Concat(lines.Select(line => line + NL));

    [Fact(DisplayName = "Detection prompt: SOURCE, CURRENT TRANSLATION, glossary block")]
    public void BuildsDetectionPrompt()
    {
        var prompt = QualityControlWorkflow.BuildQcUserPrompt("剑法威力", "Sword Power", Glossary);

        Assert.Equal(Expected(
            "SOURCE (Chinese): 剑法威力",
            "CURRENT TRANSLATION (English): Sword Power",
            "Relevant glossary terms (must be preserved if they appear in SOURCE):",
            Glossary), prompt);
    }

    [Fact(DisplayName = "Empty glossary prompt omits the glossary header entirely")]
    public void OmitsEmptyGlossaryBlock()
    {
        var prompt = QualityControlWorkflow.BuildQcUserPrompt("剑法威力", "Sword Power", string.Empty);

        Assert.Equal(Expected("SOURCE (Chinese): 剑法威力", "CURRENT TRANSLATION (English): Sword Power"), prompt);
    }

    [Fact(DisplayName = "Extra labelled fields go between CURRENT TRANSLATION and the glossary, in order")]
    public void PlacesExtraFieldsBeforeGlossary()
    {
        var prompt = QualityControlWorkflow.BuildQcUserPrompt("剑法威力", "Sword Power", Glossary,
            ("CONFIRMED DEFECTS", "DOMAIN_TERM, DROPPED_CONTENT"),
            ("PROPOSED CORRECTION", "Sword Technique Power"));

        Assert.Equal(Expected(
            "SOURCE (Chinese): 剑法威力",
            "CURRENT TRANSLATION (English): Sword Power",
            "CONFIRMED DEFECTS: DOMAIN_TERM, DROPPED_CONTENT",
            "PROPOSED CORRECTION: Sword Technique Power",
            "Relevant glossary terms (must be preserved if they appear in SOURCE):",
            Glossary), prompt);
    }

    /// <summary>Records every request's (system, user) message pair and answers with a fixed response.</summary>
    private sealed class CapturingHandler(string response) : HttpMessageHandler
    {
        public List<(string System, string User)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using (var doc = JsonDocument.Parse(body))
            {
                var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
                Requests.Add((messages[0].GetProperty("content").GetString()!, messages[1].GetProperty("content").GetString()!));
            }

            var responseJson = JsonSerializer.Serialize(new { choices = new object[] { new { message = new { content = response } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
        }
    }

    private static (LlmConfig Config, ModelExecutionConfig Model) BuildConfig()
    {
        var config = new LlmConfig { QualityControl = new QualityControlConfig { Enabled = true } };
        var model = new ModelExecutionConfig
        {
            Url = "http://test.local/v1/chat/completions",
            ApiKeyRequired = false,
            Model = "test-model",
            Prompts = new Dictionary<string, string>
            {
                ["BaseQualityControlPrompt"] = "detection system prompt",
                ["BaseQualityControlCorrectionPrompt"] = "correction system prompt",
                ["BaseQualityControlVerificationPrompt"] = "verification system prompt",
                ["BaseQualityControlCorrectionRepairPrompt"] = "repair system prompt",
            },
        };
        config.Runtime.Models["Default"] = model;
        return (config, model);
    }

    [Fact(DisplayName = "Every QC call sends its system prompt and the exact pinned user prompt")]
    public async Task EveryCallSendsPinnedPrompt()
    {
        var (config, model) = BuildConfig();
        QcDefectCategory[] defects = [QcDefectCategory.DomainTerm, QcDefectCategory.DroppedContent];

        var detectHandler = new CapturingHandler("DEFECTS: NONE");
        await QualityControlWorkflow.DetectDefectsAsync(config, model, new HttpClient(detectHandler), "raw", "剑法威力", "Sword Power", Glossary, null);

        var correctionHandler = new CapturingHandler("CORRECTED: Sword Technique Power");
        var correction = await QualityControlWorkflow.GenerateCorrectionAsync(config, model, new HttpClient(correctionHandler), "raw", "剑法威力", "Sword Power", string.Empty, defects, null);

        var verifyHandler = new CapturingHandler("UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 90");
        await QualityControlWorkflow.GetVerificationVerdictAsync(config, model, new HttpClient(verifyHandler), "raw", "剑法威力", "Sword Power", Glossary, defects, "Sword Technique Power");

        var repairHandler = new CapturingHandler("CORRECTED: NONE");
        var repair = await QualityControlWorkflow.GetCorrectionRepairAsync(config, model, new HttpClient(repairHandler), "raw", "剑法威力", "Sword Power", Glossary, [QcDefectCategory.DomainTerm], "Sword Tech Power");

        Assert.Equal(("detection system prompt", Expected(
            "SOURCE (Chinese): 剑法威力",
            "CURRENT TRANSLATION (English): Sword Power",
            "Relevant glossary terms (must be preserved if they appear in SOURCE):",
            Glossary)), Assert.Single(detectHandler.Requests));

        Assert.Equal("Sword Technique Power", correction);
        Assert.Equal(("correction system prompt", Expected(
            "SOURCE (Chinese): 剑法威力",
            "CURRENT TRANSLATION (English): Sword Power",
            "CONFIRMED DEFECTS: DOMAIN_TERM, DROPPED_CONTENT")), Assert.Single(correctionHandler.Requests));

        Assert.Equal(("verification system prompt", Expected(
            "SOURCE (Chinese): 剑法威力",
            "CURRENT TRANSLATION (English): Sword Power",
            "CONFIRMED DEFECTS: DOMAIN_TERM, DROPPED_CONTENT",
            "PROPOSED CORRECTION: Sword Technique Power",
            "Relevant glossary terms (must be preserved if they appear in SOURCE):",
            Glossary)), Assert.Single(verifyHandler.Requests));

        Assert.Null(repair);
        Assert.Equal(("repair system prompt", Expected(
            "SOURCE (Chinese): 剑法威力",
            "CURRENT TRANSLATION (English): Sword Power",
            "TARGET DEFECTS: DOMAIN_TERM",
            "PREVIOUS ATTEMPT: Sword Tech Power",
            "Relevant glossary terms (must be preserved if they appear in SOURCE):",
            Glossary)), Assert.Single(repairHandler.Requests));
    }

    [Theory(DisplayName = "Correction and repair share CORRECTED: parsing - NONE, missing label and leaks all yield null")]
    [InlineData("CORRECTED: NONE")]
    [InlineData("I think it is fine.")]
    [InlineData("CORRECTED: Sword Technique Power NONE")]
    public async Task CorrectionAndRepairRejectNoneMissingAndLeaked(string response)
    {
        var (config, model) = BuildConfig();

        var correction = await QualityControlWorkflow.GenerateCorrectionAsync(config, model, new HttpClient(new CapturingHandler(response)),
            "raw", "剑法威力", "Sword Power", string.Empty, [QcDefectCategory.DomainTerm], null);
        var repair = await QualityControlWorkflow.GetCorrectionRepairAsync(config, model, new HttpClient(new CapturingHandler(response)),
            "raw", "剑法威力", "Sword Power", string.Empty, [QcDefectCategory.DomainTerm], "Sword Tech Power");

        Assert.Null(correction);
        Assert.Null(repair);
    }
}
