using FanslationStudio.LlmKit;

namespace Tests;

/// <summary>
/// One form for wide brackets and title marks in a saved translation: a whole 【label】 becomes one ASCII [label] (never 【[label]】),
/// and a kept 《 or 》 becomes the plain quote in place (never both the wide mark and a quote).
/// </summary>
public class LineValidationWholeLineBracketTests
{
    [Theory(DisplayName = "A whole-line 【】 source is saved as one ASCII [ ] whatever the model wrote")]
    [InlineData("【任务完成】", "Task Completed", "[Task Completed]")]
    [InlineData("【任务完成】", "[Task Completed]", "[Task Completed]")]
    [InlineData("【任务完成】", "【Task Completed】", "[Task Completed]")]
    [InlineData("【任务完成】", "【[Task Completed]】", "[Task Completed]")]
    [InlineData("任务完成", "Task Completed", "Task Completed")]
    [InlineData("【任务】完成", "Task Completed", "Task Completed")]
    public void EncaseSquareBracketsForWholeLines(string raw, string translated, string expected)
    {
        Assert.Equal(expected, LineValidation.EncaseSquareBracketsForWholeLines(raw, translated));
    }

    [Theory(DisplayName = "A kept 《 or 》 on a one-sided cell becomes the plain quote, not a wide mark plus a quote")]
    [InlineData("拔刀术》", "Blade-Drawing Art》'", "Blade-Drawing Art'")]
    [InlineData("拔刀术》", "Blade-Drawing Art》", "Blade-Drawing Art'")]
    [InlineData("第三章》接话。", "》responds'", "responds'")]
    [InlineData("取自《四十二章经", "taken from the 《Forty-Two Chapter Sutra", "taken from the 'Forty-Two Chapter Sutra")]
    [InlineData("《合盘掌", "He Pan Palm", "'He Pan Palm")]
    [InlineData("《合盘掌", "《He Pan Palm", "'He Pan Palm")]
    public void FixUnbalancedQuotes_NormalisesKeptTitleMarks(string raw, string translated, string expected)
    {
        Assert.Equal(expected, LineValidation.FixUnbalancedQuotes(raw, translated));
    }
}
