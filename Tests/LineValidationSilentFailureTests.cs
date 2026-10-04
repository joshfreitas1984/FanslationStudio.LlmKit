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
            ["CorrectInventedGenderPrompt"] = "no gender",
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

    [Fact(DisplayName = "Translator commentary about a more natural English equivalent fails with a reason")]
    public void TranslatorCommentary_FailsWithReason()
    {
        var validation = Validate(
            "若诸葛姐姐愿意出手相助，定能帮咱们渡过难关的！",
            "If Big Sister Zhuge is willing to help, she will surely help us get through this crisis! However, if a more natural English equivalent is needed, it could be crisis or challenge. Challenge Stage");

        Assert.False(validation.Valid);
        Assert.Contains(validation.SilentFailures, reason => reason.Contains("'English equivalent'"));
    }

    [Theory(DisplayName = "Invented he/his on an ungendered stage direction or unnamed role sets a soft prompt but stays valid")]
    [InlineData("（笑着把银两收起来）", "(Smiling, he put the silver away)")]
    [InlineData("此人暂无性命之虞", "Don't worry, he is not in immediate danger")]
    public void InventedGender_SetsSoftPrompt_WithoutFailing(string raw, string result)
    {
        var validation = Validate(raw, result);

        Assert.True(validation.Valid);
        Assert.Equal("no gender", validation.SoftCorrectionPrompt);
    }

    [Theory(DisplayName = "No soft prompt when the source states gender, the line is running narration, or the result is already neutral")]
    [InlineData("（他笑着把银两收起来）", "(Smiling, he put the silver away)")]
    [InlineData("（笑着把银两收起来）", "(Smiling, they put the silver away)")]
    [InlineData("赵胤宗笑着把银两收了起来。", "Zhao Yinzong smiled and put his silver away")]
    [InlineData("（笑着把银子收起来）", "(Smiling, put the silver away)")]
    public void NoInventedGender_NoSoftPrompt(string raw, string result)
    {
        var validation = Validate(raw, result);

        Assert.True(validation.Valid);
        Assert.Empty(validation.SoftCorrectionPrompt);
    }

    [Fact(DisplayName = "A result that already fails a hard check does not also carry a soft prompt")]
    public void HardFailure_CarriesNoSoftPrompt()
    {
        var validation = Validate("（笑着把银两收起来）", "(Smiling, he put the silver away) 银两");

        Assert.False(validation.Valid);
        Assert.Empty(validation.SoftCorrectionPrompt);
    }

    [Fact(DisplayName = "An unclosed color tag fails with a reason")]
    public void UnclosedColorTag_FailsWithReason()
    {
        var validation = Validate("<color=red>你好</color>", "<color=red>Hello");

        Assert.False(validation.Valid);
        Assert.NotEmpty(validation.SilentFailures);
    }
}
