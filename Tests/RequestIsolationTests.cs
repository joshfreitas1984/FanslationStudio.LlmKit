using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tests;

public class RequestIsolationTests
{
    private sealed class AuthRecordingHandler : HttpMessageHandler
    {
        public ConcurrentBag<(string Url, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));

            var json = JsonSerializer.Serialize(new { choices = new object[] { new { message = new { content = "ok" } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task ConcurrentRequests_ToKeyedAndKeylessModels_NeverShareAuthHeaders()
    {
        var handler = new AuthRecordingHandler();
        using var client = new HttpClient(handler);
        var config = new LlmConfig();

        var keyed = new ModelExecutionConfig { Model = "cloud", Url = "http://cloud.test/", ApiKeyRequired = true, ApiKey = "secret" };
        var keyless = new ModelExecutionConfig { Model = "local", Url = "http://local.test/", ApiKeyRequired = false };

        var messages = new List<object> { LlmHelpers.GenerateUserPrompt("x") };
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            TranslationService.TranslateMessagesAsync(client, config, i % 2 == 0 ? keyed : keyless, messages)));

        Assert.Equal(200, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            if (r.Url.StartsWith("http://cloud.test"))
                Assert.Equal("Bearer secret", r.Auth);
            else
                Assert.Null(r.Auth);
        });
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void AppendPromptsFor_NoMatchingTerms_ReturnsEmpty()
    {
        var glossary = new List<GlossaryLine> { new("江湖", "Jianghu") };

        Assert.Equal(string.Empty, GlossaryLine.AppendPromptsFor("你好", glossary, "File.txt"));
    }

    [Fact]
    public void AppendPromptsFor_MatchingTerm_ReturnsFencedBlock()
    {
        var glossary = new List<GlossaryLine> { new("江湖", "Jianghu") };

        var prompt = GlossaryLine.AppendPromptsFor("行走江湖", glossary, "File.txt");

        Assert.StartsWith("```", prompt);
        Assert.Contains("Jianghu", prompt);
        Assert.EndsWith("```" + Environment.NewLine, prompt);
    }

    [Fact]
    public void AppendPromptsFor_RespectsOnlyAndExcludeScoping()
    {
        var glossary = new List<GlossaryLine>
        {
            new("江湖", "Jianghu") { OnlyOutputFiles = ["A.txt"] },
            new("武林", "Wulin") { ExcludeOutputFiles = ["A.txt"] },
        };

        var forA = GlossaryLine.AppendPromptsFor("江湖武林", glossary, "A.txt");
        var forB = GlossaryLine.AppendPromptsFor("江湖武林", glossary, "B.txt");

        Assert.Contains("Jianghu", forA);
        Assert.DoesNotContain("Wulin", forA);
        Assert.DoesNotContain("Jianghu", forB);
        Assert.Contains("Wulin", forB);
    }

    [Fact]
    public async Task YamlSerializer_SharedAcrossThreads_IsStable()
    {
        var serializer = YamlHelper.CreateSerializer();
        var lines = Enumerable.Range(0, 50)
            .Select(i => new TranslationLine { Raw = $"raw{i}", Splits = [new TranslationSplit { Text = $"t{i}", Translated = $"tr{i}" }] })
            .ToList();

        var expected = serializer.Serialize(lines);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => serializer.Serialize(lines))));

        Assert.All(results, r => Assert.Equal(expected, r));
    }

    [Theory]
    [InlineData("hello", false)]
    [InlineData("hello 江湖", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsCjk_MatchesPatternSemantics(string? input, bool expected)
    {
        Assert.Equal(expected, LineValidation.ContainsCjk(input));
    }
}
