using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;

namespace Tests;

/// <summary>
/// Checks in <see cref="LineValidation.CheckTransalationSuccessful"/> that add no correction prompt must
/// still say why they failed (<see cref="ValidationResult.SilentFailures"/>), and an "invalid phrase" the
/// source itself contains (a Windows path's "\U") is not a failure.
/// </summary>
public class LineValidationSilentFailureTests
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

    [Fact(DisplayName = "A Windows path in the source does not trip the \\U invalid phrase")]
    public void SourcePath_IsNotAnInvalidPhrase()
    {
        var validation = Validate(@"请查看备份文件夹：C:\Users\[用户名]\Save_backup", @"Please check the backup folder: C:\Users\[Username]\Save_backup");

        Assert.True(validation.Valid);
        Assert.Empty(validation.SilentFailures);
    }

    [Fact(DisplayName = "An invalid phrase the source lacks still fails, with a reason")]
    public void InvalidPhrase_FailsWithReason()
    {
        var validation = Validate("你好", @"Hello \U4F60");

        Assert.False(validation.Valid);
        Assert.Contains(validation.SilentFailures, reason => reason.Contains(@"'\U'"));
    }

    [Fact(DisplayName = "An unclosed color tag fails with a reason")]
    public void UnclosedColorTag_FailsWithReason()
    {
        var validation = Validate("<color=red>你好</color>", "<color=red>Hello");

        Assert.False(validation.Valid);
        Assert.NotEmpty(validation.SilentFailures);
    }
}
