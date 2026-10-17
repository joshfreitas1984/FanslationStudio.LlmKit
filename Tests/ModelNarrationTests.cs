using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;

namespace Tests;

/// <summary>
/// A translation that is the model talking about its task, or continuing the system prompt's "(Bored) Ah, ..." example into an
/// invented monologue. Real cases from PathOfKungfuOverLlm's first pass (33 lines, all later replaced by QC).
/// </summary>
public class ModelNarrationTests
{
    [Theory(DisplayName = "FindModelNarration flags the model talking about its task and ignores ordinary dialogue")]
    [InlineData("单研一道，便能反推出禁蛊之术", "Ah, ...\nI wonder what Lord Hundred Eyes is up to now.\n\nIs he alright?", true)]
    [InlineData("所以我才要硬闯净蛊天池", "Bored Ah, ...\nI wonder what Lord Hundred Eyes is up to now.", true)]
    [InlineData("刀祖怒意勃发", "Ah, I wonder what there is to do around here.", true)]
    [InlineData("为虎作伥！", "Sure, please provide the specific text that needs to be translated so I can proceed.", true)]
    [InlineData("“……你勾结螣蛇蛊主意欲何为？”", "It seems there was an instruction to correct a previous output, but no specific previous output was given.", true)]
    [InlineData("在下丁昭，初次得见大侠", "Ding Zhao appears to be addressing you as 'Hero'.", true)]
    [InlineData("招式便罢", "Ah, ... I guess there's nothing else to do here. Let me see if there's anything interesting around town.", true)]
    [InlineData("话音落下", "Ah, ... I can't seem to find anything interesting here. Maybe Liu Shuo knows something.", true)]
    [InlineData("甲", "Ah, ... I wonder what's going on outside. Maybe I should take a look.", true)]
    [InlineData("乙", "Ah, I wonder what I should do next.", true)]
    [InlineData("丙", "Bored, ah, ... I guess there's nothing else to do here. Maybe I should head back.", true)]
    [InlineData("丁", "Ah, ... I wonder what Qi You is up to now.", true)]
    [InlineData("戊", "The text you provided doesn't contain any additional content. Could you please provide the specific text you'd like translated?", true)]
    [InlineData("己", "Among the Daqi Sect, perhaps only one person... If there's no specific glossary entry for it in this context, a natural translation is", true)]
    [InlineData("乙", "Ah, ...\nI wonder what Lord Hundred Eyes is up to now.", true)]
    [InlineData("哎，……", "Ah, ... I wonder what happened to him.", false)]
    [InlineData("哎，……", "Ah, ... forget it.", false)]
    [InlineData("这是个好主意", "I wonder what the Master will say about this.", false)]
    [InlineData("此地，便是弃身冢。", "Here is the Abandoned Tomb.", false)]
    [InlineData("想来此事并不简单", "It seems this matter is not so simple.", false)]
    public void FindModelNarration_Cases(string raw, string result, bool narration) =>
        Assert.Equal(narration, LineValidation.FindModelNarration(raw, result) != null);

    [Fact(DisplayName = "Narration fails validation with a reason, so it is retried and a QC correction like it is rejected")]
    public void Narration_FailsValidation()
    {
        var validation = LineValidation.CheckTransalationSuccessful(new ModelExecutionConfig
        {
            Url = "http://test.local/v1/chat/completions",
            ApiKeyRequired = false,
            Model = "test-model",
            Prompts = new Dictionary<string, string>(),
        }, "刀祖怒意勃发，似是想说什么", "Ah, I wonder what there is to do around here.", new TextFileToSplit { Path = "Test.txt", TextFileType = TextFileType.RawCsv });

        Assert.False(validation.Valid);
        Assert.Contains(validation.SilentFailures, reason => reason.Contains("talking about the task"));
    }
}
