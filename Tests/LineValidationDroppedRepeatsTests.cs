using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace Tests;

/// <summary><see cref="NamedTokenReplacer.AllowDroppedRepeats"/>: a repeated slot may collapse to one, but not vanish.</summary>
[Collection("StringTokenReplacerStatics")]
public class LineValidationDroppedRepeatsTests : IDisposable
{
    public void Dispose() => StringTokenReplacer.SetNamedTokens([], []);

    private static void Configure(bool allowDroppedRepeats) =>
        StringTokenReplacer.SetNamedTokens(
        [
            new() { Pattern = @"<[^<>|]*\|\|[^<>|]*>", Name = "playerTitle", RestoreFromGlossary = true, AllowDroppedRepeats = allowDroppedRepeats },
            new() { Literal = "<fName>", Name = "firstName" },
        ], []);

    private static ValidationResult Validate(string raw, string result) =>
        LineValidation.CheckTransalationSuccessful(
            new ModelExecutionConfig
            {
                Url = "http://test.local/v1/chat/completions",
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
            },
            raw, result, new TextFileToSplit { Path = "Test.txt", TextFileType = TextFileType.RawCsv });

    [Theory(DisplayName = "A repeated slot only has to survive once when allowDroppedRepeats is on")]
    [InlineData(true, "{playerTitle} spoke to {playerTitle}", "{playerTitle} spoke to you", true)]
    [InlineData(true, "{playerTitle} spoke to {playerTitle}", "You spoke to me", false)]
    [InlineData(true, "{firstName} spoke to {firstName}", "{firstName} spoke to you", false)]
    [InlineData(false, "{playerTitle} spoke to {playerTitle}", "{playerTitle} spoke to you", false)]
    [InlineData(true, "{playerTitle} spoke to {playerTitle}", "{playerTitle} spoke to {playerTitle} {playerTitle}", false)]
    public void RepeatedSlotMayCollapse(bool allow, string raw, string result, bool valid)
    {
        Configure(allow);

        Assert.Equal(valid, Validate(raw, result).Valid);
    }
}
