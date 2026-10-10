using FanslationStudio.LlmKit;

namespace Tests;

/// <summary>The correction-prompt echo that once produced "While correcting, also verify: ..." in translations.</summary>
public class PromptLeakTests
{
    [Theory(DisplayName = "FindPromptLeak flags echoed prompt text and ignores ordinary English")]
    [InlineData("那小姑娘哭了", "While correcting, also verify: The girl cried", "While correcting,")]
    [InlineData("那小姑娘哭了", "The girl cried. While correcting, she wiped her eyes.", "While correcting,")]
    [InlineData("好", "Output only the corrected line", "Output only the")]
    [InlineData("好", "Gender" + "‑" + "neutral language is used", "gender-neutral language")]
    [InlineData("他纠正了姿势", "He fixed his stance while correcting the form", null)]
    [InlineData("他纠正了姿势", "He sighed while correcting, again, his stance", null)]
    [InlineData("While correcting, 好", "While correcting, fine", null)]
    [InlineData("大人放心，咱们船上有水老坐镇。", "Translate the following sentence to English while keeping the rest intact.", "Translate the following sentence")]
    [InlineData("你又拿我的酒做人情了吧？", "You're using my wine as a favor again? Translate the following sentence to English while keeping the rest intact.", "Translate the following sentence")]
    [InlineData("请翻译", "Please translate the sentence below", null)]
    [InlineData("好", "", null)]
    public void FindPromptLeak_Cases(string raw, string result, string? expected)
    {
        Assert.Equal(expected, LineValidation.FindPromptLeak(raw, result));
    }
}
