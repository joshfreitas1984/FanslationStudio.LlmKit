using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Tests;

/// <summary>
/// Mocks the LLM HTTP endpoint (<see cref="TranslationService.TranslateMessagesAsync"/> POSTs to
/// <see cref="ModelUrlConfig.Url"/> and parses an OpenAI-style "choices[0].message.content"
/// response) so <see cref="TranslationService"/> helpers can be exercised without a real LLM. Rules
/// are matched in order against the concatenation of every message's content in the request (not
/// just the last "user" message, since some call sites put the actual text-to-translate in an
/// "assistant" message), and each rule returns its next scripted response on every call (repeating
/// the final response if called more times than it has scripted responses), tracking how many
/// times it was called.
/// </summary>
public sealed class ScriptedLlmHandler : HttpMessageHandler
{
    public sealed class Rule
    {
        public required Func<string, bool> Matches { get; init; }
        public required string[] Responses { get; init; }
        public int CallCount { get; private set; }

        public string Next()
        {
            var index = CallCount;
            CallCount++;
            return index < Responses.Length ? Responses[index] : Responses[^1];
        }
    }

    private readonly List<Rule> _rules;

    public ScriptedLlmHandler(List<Rule> rules)
    {
        _rules = rules;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);

        // Match against every message's content (not just the last "user" message) - some call
        // sites (e.g. CorrectSentenceBySentenceAsync) put the actual text-to-translate in an
        // "assistant" message and only generic instructions in the final "user" message.
        string allContent;
        using (var doc = JsonDocument.Parse(body))
        {
            allContent = string.Join('\n', doc.RootElement.GetProperty("messages")
                .EnumerateArray()
                .Select(message => message.GetProperty("content").GetString() ?? string.Empty));
        }

        var rule = _rules.FirstOrDefault(r => r.Matches(allContent))
            ?? throw new InvalidOperationException($"No scripted rule matched request content: {allContent}");

        var content = rule.Next();
        var responseJson = JsonSerializer.Serialize(new { choices = new object[] { new { message = new { content } } } });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
    }
}

public class TranslationServiceTests
{
    private static LlmConfig BuildConfig(List<ScriptedLlmHandler.Rule> rules, out HttpClient client, int retryCount = 1)
    {
        var config = new LlmConfig
        {
            RetryCount = retryCount,
            CorrectionPromptsEnabled = false,
            SkipLineValidation = false,
        };

        config.Runtime.Models["Default"] = new ModelExecutionConfig
        {
            Url = "http://test.local/v1/chat/completions",
            ApiKeyRequired = false,
            Model = "test-model",
            Prompts = new Dictionary<string, string>
            {
                ["BaseSystemPrompt"] = "system prompt",
                ["BaseCorrectionSuffixPrompt"] = "correction suffix",
                ["CorrectChinesePrompt"] = "correct chinese",
                ["CorrectAlternativesPrompt"] = "{0}",
                ["CorrectExplainationPrompt"] = "explain",
                ["CorrectColonSegementPrompt"] = "colon",
                ["CorrectRemovalPrompt"] = "{0}",
                ["CorrectRemovedQuotesPrompt"] = "quotes",
                ["CorrectAdditionalPrompt"] = "{0}",
            },
        };

        client = new HttpClient(new ScriptedLlmHandler(rules));
        return config;
    }

    private static TextFileToSplit BuildTextFile() => new()
    {
        Path = "Test.txt",
        TextFileType = TextFileType.RegularDb,
        EnableGlossary = false,
        EnableBasePrompts = false,
        AdditionalPromptName = string.Empty,
    };

    [Fact(DisplayName = "SplitOnCharsIfNeededAsync retries only the piece that failed validation")]
    public async Task SplitOnCharsIfNeededAsync_RetriesOnlyFailedPiece()
    {
        var helloRule = new ScriptedLlmHandler.Rule
        {
            Matches = c => c.Contains("你好"),
            Responses = ["Hello"],
        };
        var worldRule = new ScriptedLlmHandler.Rule
        {
            Matches = c => c.Contains("世界"),
            // First attempt is an invalid phrase (LineValidation.InvalidPhrases) so it fails
            // validation and must be retried; second attempt is a normal translation. The retry
            // itself comes from TranslateSplitAsync's own internal retry loop (retryCount: 2 below
            // gives it two attempts) - TranslatePiecesWithRetryAsync no longer adds a second,
            // redundant retry loop on top of that (see its doc comment for why that was a bug).
            Responses = ["please provide the Chinese string", "World"],
        };

        var config = BuildConfig([helloRule, worldRule], out var client, retryCount: 2);
        var textFile = BuildTextFile();

        var (split, result) = await TranslationService.SplitOnCharsIfNeededAsync("|", config, "你好|世界", client, textFile);

        Assert.True(split);
        Assert.Equal("Hello|World", result);

        // The already-good piece must not be re-sent to the LLM just because its sibling failed.
        Assert.Equal(1, helloRule.CallCount);
        Assert.Equal(2, worldRule.CallCount);
    }

    [Fact(DisplayName = "SplitBracketsRegexIfNeededAsync restores the actual translated bracket content")]
    public async Task SplitBracketsRegexIfNeededAsync_RestoresTranslatedBracketContent()
    {
        // Regression test for a bug where the translated bracket content (innerTranslations) was
        // computed but discarded, and the final result was reconstructed from a mangled substring
        // of the internal placeholder number instead - so translated bracket text never actually
        // made it into the output.
        var innerRule = new ScriptedLlmHandler.Rule
        {
            Matches = c => c.Contains("世界") && !c.Contains("{0}"),
            Responses = ["World"],
        };
        var fullSentenceRule = new ScriptedLlmHandler.Rule
        {
            Matches = c => c.Contains("{0}"),
            Responses = ["Hello {0}"],
        };

        var config = BuildConfig([innerRule, fullSentenceRule], out var client);
        config.SplitRegexPatterns = ["《.*?》"];
        var textFile = BuildTextFile();

        var (split, result) = await TranslationService.SplitBracketsRegexIfNeededAsync(config, "你好《世界》", client, textFile);

        Assert.True(split);
        // Must contain the actual translated bracket content, wrapped in the original brackets -
        // not an empty/mangled placeholder artifact.
        Assert.Contains("《World》", result);
        Assert.Equal(1, innerRule.CallCount);
        Assert.Equal(1, fullSentenceRule.CallCount);
    }

    [Fact(DisplayName = "CorrectSentenceBySentenceAsync does not split sentences inside quoted clauses")]
    public async Task CorrectSentenceBySentenceAsync_DoesNotSplitInsideQuotes()
    {
        // Regression test for the naive `Split(". ")` which would have cut this failed result
        // into three pieces (breaking apart the quoted clause "Hello. World"), rather than the two
        // real sentences it actually contains.
        var chineseSentenceRule = new ScriptedLlmHandler.Rule
        {
            Matches = c => c.Contains("你好."),
            Responses = ["Hello."],
        };

        var config = BuildConfig([chineseSentenceRule], out var client);
        var modelConfig = config.Runtime.Models["Default"];
        var textFile = BuildTextFile();

        var failedResult = "He said \"Hello. World\" to me. 你好.";

        var result = await TranslationService.CorrectSentenceBySentenceAsync(client, config, modelConfig, failedResult, failedResult, textFile);

        Assert.Equal("He said \"Hello. World\" to me. Hello.", result);
        // The quoted clause's sentence never contained Chinese, so only the trailing Chinese
        // sentence should have triggered an LLM call.
        Assert.Equal(1, chineseSentenceRule.CallCount);
    }

    [Fact(DisplayName = "TranslateSplitAsync retries only the sentence-correction pass, not a full whole-cell retranslation")]
    public async Task TranslateSplitAsync_SentenceCorrectionRetry_DoesNotFallBackToWholeCellRetranslation()
    {
        // Regression test for the outer retry loop discarding an already-mostly-correct
        // sentence-by-sentence correction and re-translating the whole cell from scratch. The
        // fix keeps re-invoking CorrectSentenceBySentenceAsync (which only re-translates
        // sentences that still contain Chinese) instead of falling back to a fresh whole-cell
        // TranslateMessagesAsync call on every failed correction attempt. There is deliberately no
        // internal per-sentence retry loop inside CorrectSentenceBySentenceAsync itself (that was
        // a redundant multiplier on top of this outer retry, since both were retrying the exact
        // same still-broken sentence) - the whole sentence-correction retry budget is spent by this
        // outer loop alone, one CorrectSentenceBySentenceAsync call per attempt.
        var fullCellRule = new ScriptedLlmHandler.Rule
        {
            // Only the initial whole-cell translation request contains the raw Chinese source
            // text - correction-pass messages never include it.
            Matches = c => c.Contains("你好世界"),
            // Deliberately still has a leftover Chinese word, triggering sentence-by-sentence
            // correction instead of an immediate success.
            Responses = ["Hello 世界."],
        };
        var sentenceCorrectionRule = new ScriptedLlmHandler.Rule
        {
            Matches = c => c.Contains("Hello 世界"),
            // RetryCount=3 -> up to 3 CorrectSentenceBySentenceAsync attempts total (one call per
            // attempt, no internal retries); first two still fail, third succeeds.
            Responses = ["Hello 世界.", "Hello 世界.", "Hello World."],
        };

        var config = BuildConfig([fullCellRule, sentenceCorrectionRule], out var client, retryCount: 3);
        config.CorrectionPromptsEnabled = true;
        var textFile = BuildTextFile();

        var result = await TranslationService.TranslateSplitAsync(config, "你好世界。", client, textFile);

        Assert.True(result.Valid);
        // Trailing full stop is stripped by LineValidation.CleanupLineBeforeSaving's
        // RemoveExtraFullStop pass (unrelated to this fix) since raw's "。" isn't treated the same
        // as an explicit "." in result.
        Assert.Equal("Hello World", result.Result);

        // The whole-cell translation request must only ever be sent once - every subsequent
        // attempt should go through sentence-level correction only, and each sentence-correction
        // attempt should cost exactly one call (no internal retry multiplier).
        Assert.Equal(1, fullCellRule.CallCount);
        Assert.Equal(3, sentenceCorrectionRule.CallCount);
    }
}

