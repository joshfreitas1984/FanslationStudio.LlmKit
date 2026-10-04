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

    [Theory(DisplayName = "NarratesAsFirstPerson flags subject-less narration written as I, not speech or thoughts")]
    [InlineData("只见阮芷躺在一块草席上，正不住咳嗽。", "I see Ruan Zhi lying on a straw mat, coughing.", true)]
    [InlineData("话音刚落，只听闻金鼓齐鸣，无数马蹄声轰隆而至。", "The moment the words fell silent, I heard gongs and drums.", true)]
    [InlineData("（只听得一阵洪钟般的笑声从山下传来", "(I could hear a loud laugh from below the mountain.", true)]
    [InlineData("只见阮芷躺在一块草席上，正不住咳嗽。", "You see Ruan Zhi lying on a straw mat.", false)]
    [InlineData("只见我宋廉庭在此，谁敢放肆！", "I, Song Lianting, am here - who dares?", false)]
    [InlineData("听闻近来监牢内更换了一批守卫，刚熟络起来的几位狱卒都调离了此处。", "I heard that a batch of new guards has been assigned.", false)]
    [InlineData("（啊呀，又扔偏了）", "(Oops, I missed again.)", false)]
    [InlineData("眼见你快步追上，只能扑通一声跪地求饶", "Seeing you catch up, I could only fall to my knees and beg.", false)]
    public void NarratesAsFirstPerson_Cases(string raw, string result, bool expected) =>
        Assert.Equal(expected, LineValidation.NarratesAsFirstPerson(raw, result));

    [Theory(DisplayName = "ContradictsGender flags only the opposite pronoun for a known gender, and never when the source states a gender")]
    [InlineData("（揉了揉被震麻的手腕）", "(Rubbing her numbed wrist)", "male", true)]
    [InlineData("（揉了揉被震麻的手腕）", "(Rubbing his numbed wrist)", "male", false)]
    [InlineData("（点了点头）", "(Smiled, he nodded)", "female", true)]
    [InlineData("（点了点头）", "(Smiled, she nodded)", "female", false)]
    [InlineData("（点了点头）", "(Smiled and nodded)", "female", false)]
    [InlineData("（点了点头）", "(Smiled, they nodded)", "male", false)]
    // The speaker is male but the source says 她: another person, so "she" is correct.
    [InlineData("（看着姜婉，她羞红了脸）", "(Looking at Jiang Wan, she blushed)", "male", false)]
    [InlineData("（看着姜婉的妹妹）", "(Looking at Jiang Wan's sister, her eyes shining)", "male", false)]
    public void ContradictsGender_Cases(string raw, string translated, string gender, bool expected) =>
        Assert.Equal(expected, LineValidation.ContradictsGender(raw, translated, gender));

    [Theory(DisplayName = "ContradictsGenderDespiteKinshipTerm flags a kinship-term source only when it has no explicit 他/她")]
    [InlineData("（妹妹说错了）", "(His younger sister was wrong)", "female", true)]
    [InlineData("（妹妹说错了）", "(Her younger sister was wrong)", "female", false)]
    [InlineData("（看着妹妹，她羞红了脸）", "(Looking at his sister, she blushed)", "male", false)]
    [InlineData("（点了点头）", "(Smiled, he nodded)", "female", false)]
    public void ContradictsGenderDespiteKinshipTerm_Cases(string raw, string translated, string gender, bool expected) =>
        Assert.Equal(expected, LineValidation.ContradictsGenderDespiteKinshipTerm(raw, translated, gender));

    [Theory(DisplayName = "NamesSomeone sees a capitalised name mid-sentence, not a sentence start or I")]
    [InlineData("Xiao Mei charged, she trembled", true)]
    [InlineData("Haha, Shangguan will not bend", true)]
    [InlineData("Who do you want Xue Ruyi to become", true)]
    [InlineData("(Smiling, he put the silver away)", false)]
    [InlineData("This person is not in danger. He is fine", false)]
    [InlineData("I will go and he will stay", false)]
    public void NamesSomeone_Cases(string translated, bool expected) =>
        Assert.Equal(expected, LineValidation.NamesSomeone(translated));

    [Theory(DisplayName = "InventsGender ignores summons and indefinites, and optionally lines that name someone")]
    [InlineData("来人，把这个家伙拖出去！", "Guards, drag him out!", false, false)]
    [InlineData("有人在门外偷听。", "Someone is eavesdropping, and he is nervous.", false, false)]
    [InlineData("此人功法诡异。", "This person's method is strange, he is skilled.", false, true)]
    [InlineData("此人功法诡异。", "Xue Ruyi saw this person; he is skilled.", false, true)]
    [InlineData("此人功法诡异。", "Xue Ruyi saw this person; he is skilled.", true, false)]
    public void InventsGender_RolesAndProse(string raw, string result, bool skipNamed, bool expected) =>
        Assert.Equal(expected, LineValidation.InventsGender(raw, result, skipNamed));

    [Theory(DisplayName = "InventsGender treats a he/she near an unknown-gender token as invented, whatever the line's shape")]
    [InlineData("#PlayerName#手脚挺快，", "#PlayerName# is quick on his feet", true)]
    [InlineData("#PlayerName#手脚挺快，", "#PlayerName#, you're quick-handed,", false)]
    [InlineData("#PlayerName#手脚挺快，", "#PlayerName# is quick on their feet", false)]
    // The translation names someone else, so the pronoun may be theirs, not the token's.
    [InlineData("还望#PlayerName#速来，皇甫掌门告辞！", "I hope #PlayerName# comes soon; Sect Master Huangfu takes his leave", false)]
    // The source states a gender itself, so the pronoun follows the source.
    [InlineData("#PlayerName#的师兄手脚挺快，", "#PlayerName#'s senior brother is quick on his feet", false)]
    // A faction token is not in the list: no person, no trigger.
    [InlineData("#PlayerForceName#手脚挺快，", "#PlayerForceName# is quick on his feet", false)]
    public void InventsGender_UnknownGenderTokens(string raw, string result, bool expected) =>
        Assert.Equal(expected, LineValidation.InventsGender(raw, result, false, ["#PlayerName#", "#$PlayerName#"]));

    [Fact(DisplayName = "InventsGender without a token list does not treat a plain token line as invented")]
    public void InventsGender_NoTokenList_NoTrigger() =>
        Assert.False(LineValidation.InventsGender("#PlayerName#手脚挺快，", "#PlayerName# is quick on his feet"));

    [Theory(DisplayName = "UsesOnlyNeutralPronouns needs a they/their and no he/she")]
    [InlineData("(Smiled, they nodded)", true)]
    [InlineData("(Smiled, he nodded)", false)]
    [InlineData("(Smiled and nodded)", false)]
    [InlineData("(They smiled; she nodded)", false)]
    public void UsesOnlyNeutralPronouns_Cases(string translated, bool expected) =>
        Assert.Equal(expected, LineValidation.UsesOnlyNeutralPronouns(translated));

    [Fact(DisplayName = "An unclosed color tag fails with a reason")]
    public void UnclosedColorTag_FailsWithReason()
    {
        var validation = Validate("<color=red>你好</color>", "<color=red>Hello");

        Assert.False(validation.Valid);
        Assert.NotEmpty(validation.SilentFailures);
    }
}
