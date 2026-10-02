using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;

namespace Tests;

public class LineValidationPlaceholderBracketTests
{
    private static ModelExecutionConfig BuildConfig() => new()
    {
        Url = "http://test.local/v1/chat/completions",
        ApiKeyRequired = false,
        Model = "test-model",
        Prompts = new Dictionary<string, string>
        {
            ["CorrectChinesePrompt"] = "correct chinese",
            ["CorrectAlternativesPrompt"] = "alt {0}",
            ["CorrectExplainationPrompt"] = "explain",
            ["CorrectRemovalPrompt"] = "removed {0}",
            ["CorrectRemovedQuotesPrompt"] = "quotes",
            ["CorrectAdditionalPrompt"] = "added {0}",
        },
    };

    private static ValidationResult Validate(string raw, string result) =>
        LineValidation.CheckTransalationSuccessful(BuildConfig(), raw, result, new TextFileToSplit
        {
            Path = "Test.txt",
            TextFileType = TextFileType.RawCsv,
        });

    [Theory(DisplayName = "Placeholder counts must match between raw and result")]
    [InlineData("{0} defeated {1}", "{0} defeated {1}", true, null)]
    [InlineData("{0} defeated", "{0} defeated {1}", false, "added {1}")]
    [InlineData("{0} defeated {1}", "{0} defeated {1} {1}", false, "added {1}")]
    [InlineData("{0} spoke to {0}", "{0} spoke", false, "removed {0}")]
    [InlineData("{0} defeated {1}", "{0} was defeated", false, "removed {1}")]
    [InlineData("Scripture Pavilion", "Scripture Pavilion {0}", false, "added {0}")]
    public void PlaceholderCountsMustMatch(string raw, string result, bool valid, string? expectedPrompt)
    {
        var validation = Validate(raw, result);

        Assert.Equal(valid, validation.Valid);
        if (expectedPrompt != null)
            Assert.Contains(expectedPrompt, validation.CorrectionPrompt);
    }

    [Theory(DisplayName = "Wide brackets in raw must not vanish from the result")]
    [InlineData("【任务】完成", "[Quest] Complete", true)]
    [InlineData("【任务】完成", "【Quest】 Complete", true)]
    [InlineData("【任务】完成", "Quest Complete", true)]
    [InlineData("「你好」", "\"Hello\"", true)]
    [InlineData("「你好」", "Hello", true)]
    [InlineData("藏书《九阳》", "Scripture 《Nine Yang》", true)]
    [InlineData("藏书《九阳》", "Scripture Nine Yang", true)]
    [InlineData("（无趣）哎，食之无味弃之可惜，", "Ah, it's boring, neither worth eating nor worth throwing away.", false)]
    [InlineData("（无趣）哎，食之无味弃之可惜，", "(Bored) Ah, neither worth eating nor worth throwing away.", true)]
    [InlineData("没有括号", "No brackets", true)]
    public void WideBracketsMustNotBeDropped(string raw, string result, bool bracketKept)
    {
        Assert.Equal(bracketKept, LineValidation.FindDroppedWideBracket(raw, result) == null);
    }

    [Fact(DisplayName = "Dropped wide bracket falls back to the removal prompt when no bracket prompt is configured")]
    public void DroppedWideBracketFallsBackToRemovalPrompt()
    {
        var validation = Validate("（无趣）哎，食之无味弃之可惜，", "Ah, it's boring, neither worth eating nor worth throwing away.");

        Assert.False(validation.Valid);
        Assert.Contains("removed （", validation.CorrectionPrompt);
    }

    [Fact(DisplayName = "Dropped wide bracket uses the dedicated bracket prompt when configured")]
    public void DroppedWideBracketUsesDedicatedPrompt()
    {
        var config = BuildConfig();
        config.Prompts["CorrectWideBracketPrompt"] = "keep bracket {0}";

        var validation = LineValidation.CheckTransalationSuccessful(config, "（无趣）哎，食之无味弃之可惜，",
            "Ah, it's boring, neither worth eating nor worth throwing away.",
            new TextFileToSplit { Path = "Test.txt", TextFileType = TextFileType.RawCsv });

        Assert.False(validation.Valid);
        Assert.Contains("keep bracket （", validation.CorrectionPrompt);
        Assert.DoesNotContain("removed", validation.CorrectionPrompt);
    }
}
