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
    // 青年 states a gender itself, so "young man" / "his" are not invented.
    [InlineData("（只见那青年从怀中掏出细毫，", "(You saw the young man take out a fine brush from his robes,")]
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

    [Theory(DisplayName = "LosesSelfReference flags a title+我 or 我+name placeholder whose speaker became a third party")]
    // title + 我 translated with no first person at all: the speaker turned into someone else
    [InlineData("万万不能松散懈怠，堕了师傅我的威名！", "You must not be lax or negligent; let down the reputation of your Master!", true)]
    [InlineData("若惹得师父他老人家生气，可别怪师姐我没提醒你。", "If you anger Master, don't blame Senior Sister for not warning you.", true)]
    // a first-person word anywhere in the translation leaves it alone (deliberately narrow)
    [InlineData("若你能在切磋之中胜过师傅我，那为师方能准许你出师！", "If you can defeat your Master in this exchange of skills, then I will allow you to leave!", false)]
    // ... unless the title is rendered as the speaker's relation or stuck onto "I"
    [InlineData("当年师伯我，确实是仙霞派掌门的继任人选。", "Back then, my senior uncle was indeed the successor to the Xianxia Sect Leader.", true)]
    [InlineData("当年师伯我，确实是仙霞派掌门的继任人选。", "Back then, my Martial Uncle was indeed the successor.", true)]
    [InlineData("云裳，别哭了别哭了，好像师姐我要死了一样。", "Yunshang, stop crying, as if Senior Sister I am about to die.", true)]
    [InlineData("假以时日，定也能达到师兄我这般实力。", "Given enough time, you'd surely reach my senior brother's level of power.", true)]
    // "my <title>" counts only for the title that follows 我: "my Master" in a 师兄我 line is a different person
    [InlineData("回想当年，#PlayerName#被师傅责骂，向师兄我求教修炼之法，", "Recalling when #PlayerName# was scolded by my Master and asked me, Senior Brother, for advice,", false)]
    // plural second-person words (各位, 诸位, 二位) are a source for "your"
    [InlineData("感谢各位同道看在我#$SourceInteractName#三分薄面上赏光莅临，", "Thank you all for gracing me with your presence, #$SourceInteractName#.", false)]
    [InlineData("我#$PlayerName#绝不会忘记各位之恩义。", "I will never forget your kindness to me, #$PlayerName#.", false)]
    // another person placeholder in the source is the addressed person, so "your" has a source
    [InlineData("#PlayerName#临危救难，恩深情重，我#$TargetInteractName#定当报答。", "#PlayerName# rescued me in danger. I, #$TargetInteractName#, will repay your kindness.", false)]
    [InlineData("万万不能松散懈怠，堕了师傅我的威名！", "You must not be lax, or you will tarnish my name as your Master!", false)]
    [InlineData("若敢藏私，师姐我定饶不了你！", "If you dare to hold back, I, your Senior Sister, won't forgive you!", false)]
    [InlineData("莫要逞强，师姐我定能保你安全。", "Don't force yourself, I, your Senior Sister, will ensure your safety.", false)]
    // 我 + person placeholder, no 你 in the source: "your" is a person shift
    [InlineData("这这这……该死小贼竟如此下作，可别落到我#$PlayerName#手里！", "Y-y-you... don't let him fall into your hands, #$PlayerName#!", true)]
    [InlineData("这这这……该死小贼竟如此下作，可别落到我#$PlayerName#手里！", "Y-y-you... don't let that thief fall into my hands, #$PlayerName#!", false)]
    [InlineData("我#$PlayerName#愿与你共抗阎罗殿！", "I, #$PlayerName#, will stand with your sect against Yama Hall!", false)]
    // correct renderings are not flagged
    [InlineData("不过，掌门我还准备了一份特殊礼物。", "However, I, the Sect Leader, have prepared a special gift.", false)]
    [InlineData("本场考核就由掌门我亲自主持好了。", "Let me, the Sect Leader, personally conduct this assessment.", false)]
    [InlineData("若惹得师父他老人家生气，可别怪师姐我没提醒你。", "If you anger Master, don't blame me, your Senior Sister, for not warning you.", false)]
    // a title with no 我 touching it, and 师姐我们 (we), are not self-references
    [InlineData("师傅，请你指点我一二。", "Master, please guide me a bit.", false)]
    [InlineData("师傅请你指点一二。", "Master, please guide them.", false)]
    [InlineData("师姐我们一起去吧。", "Let's go together, all of us.", false)]
    // 我 + name placeholder: "I #Placeholder#" with no comma treats the placeholder as another person
    [InlineData("我#$PlayerName#初入江湖，似乎并不识得老人家您这般人物。", "I #$PlayerName# is new to the Jianghu and doesn't seem to recognize someone like you.", true)]
    [InlineData("今日我#$PlayerName#就要替天行道，将你捉拿归案！", "Today, I #$PlayerName# will take justice into my own hands and arrest you!", true)]
    [InlineData("今日我#$PlayerName#就要替天行道，将你捉拿归案！", "Today, I, #$PlayerName#, will uphold justice and arrest you!", false)]
    [InlineData("哼，敢坏我#$PlayerName#的好事？", "Hmph, dare you ruin #$PlayerName#'s plans?", false)]
    [InlineData("你好，#PlayerName#。", "Hello, #PlayerName#.", false)]
    // a force/sect placeholder is a group, not a person: "I #PlayerForceName#" is not the person-name defect
    [InlineData("何况我#PlayerForceName#与逐鹿盟，本就有不少恩怨在先。", "Moreover, I #PlayerForceName# had many grudges with the Zhulu Alliance.", false)]
    public void LosesSelfReference_Cases(string raw, string result, bool expected) =>
        Assert.Equal(expected, LineValidation.LosesSelfReference(raw, result));

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
    // A second person named only by a role (老乞丐, 老兵) can own the pronoun; a plain hero line is still checked.
    [InlineData("（那老乞丐颤颤巍巍把那铁碗举到面前，林云裳定睛一看，", "(The old beggar raised the iron bowl to his face. Lin Yunshang took a closer look,", "female", false)]
    [InlineData("这老兵武功出神入化，眼看就要将上官凤斩于枪下。", "This old veteran was about to cut Shangguan Feng down with his spear.", "female", false)]
    [InlineData("（白云天话说到一半，面色突变，", "(Bai Yuntian's words were cut off as his expression changed,", "female", true)]
    public void ContradictsGender_Cases(string raw, string translated, string gender, bool expected) =>
        Assert.Equal(expected, LineValidation.ContradictsGender(raw, translated, gender));

    [Theory(DisplayName = "ContradictsGenderDespiteKinshipTerm flags a kinship-term source only when it has no explicit 他/她")]
    [InlineData("（妹妹说错了）", "(His younger sister was wrong)", "female", true)]
    [InlineData("（妹妹说错了）", "(Her younger sister was wrong)", "female", false)]
    [InlineData("（看着妹妹，她羞红了脸）", "(Looking at his sister, she blushed)", "male", false)]
    [InlineData("（点了点头）", "(Smiled, he nodded)", "female", false)]
    // The pronoun matches the kinship term's own gender: it belongs to the relative, not the speaker.
    [InlineData("（跪下）哈哈，这小师妹还没入门，怎就如此受欢迎了？", "(Kneeling) Ha ha, this junior sister hasn't even joined yet, and she's already so popular?", "male", false)]
    [InlineData("（大哥说错了）", "(His elder brother was wrong)", "female", false)]
    [InlineData("（还好小妮子没反应过来，否则非得让我退学费不可）", "(Good thing the little girl didn't react, or I'd refund her tuition)", "male", false)]
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
    [InlineData("街边乞丐一遍以筷子敲碗，一遍所唱之歌谣，其吐字换气中暗含丐帮入门内功心法", "A beggar by the street beat on his bowl while singing; his diction concealed the method.", false, true)]
    [InlineData("街边乞丐一遍以筷子敲碗，一遍所唱之歌谣，其吐字换气中暗含丐帮入门内功心法", "A beggar by the street beat on a bowl while singing; the beggar's diction concealed the method.", false, false)]
    public void InventsGender_RolesAndProse(string raw, string result, bool skipNamed, bool expected) =>
        Assert.Equal(expected, LineValidation.InventsGender(raw, result, skipNamed));

    [Theory(DisplayName = "SourceStatesGender recognises gendered role nouns (old man, old woman, madam, monk)")]
    [InlineData("想那老头万一气再长些", true)]
    [InlineData("何况是六旬老妪！", true)]
    [InlineData("老鸨还说下回见就要打死我", true)]
    [InlineData("这老和尚怕不是自命武林至尊吧", true)]
    [InlineData("魏掌门为人谦和", false)]
    public void SourceStatesGender_Roles(string raw, bool expected) =>
        Assert.Equal(expected, LineValidation.SourceStatesGender(raw));

    [Theory(DisplayName = "A name ending in He (Chao He) is not read as the pronoun he")]
    [InlineData("此人给我们打个半死才招供，名叫晁和来着。", "We beat them half to death before they confessed; their name is Chao He.", false)]
    [InlineData("此人给我们打个半死才招供，名叫晁和来着。", "We beat him half to death before he confessed; his name is Chao He.", true)]
    [InlineData("名唤晁和的此人", "This person, named Chao He. He fled.", true)]
    public void InventsGender_NameEndingInHe(string raw, string result, bool expected) =>
        Assert.Equal(expected, LineValidation.InventsGender(raw, result));

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
