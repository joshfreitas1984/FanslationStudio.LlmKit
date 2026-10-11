using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace Tests;

/// <summary>
/// <see cref="TokenSlotter"/>: slotting a game's runtime tokens into named slots with a per-slot value map, and restoring them from
/// that map alone. It holds no state, so none of these tests share anything.
/// </summary>
public class TokenSlotterTests
{
    private const string Title = "<少侠||姑娘>";
    private const string OtherTitle = "<公子||姑娘>";

    private static readonly List<GlossaryLine> Glossary =
    [
        new(Title, "<Young Hero||Miss>"),
        new(OtherTitle, "<Young Master||Miss>"),
    ];

    private const string CodeLetterContext = @"(?:(?<=[^\x00-\x7F])|(?<=[>,.!?:;()])|^)";
    private const string CodeLetterEnd = @"(?=[^\x00-\x7F]|[<,.!?:;()\[]|$)";

    private static TokenSlotter Slotter(params NamedTokenReplacer[] extra) => new(
        [
            new() { Literal = "<fName>", Name = "firstName", Description = "the player's first name." },
            new() { Pattern = @"<[^<>|]*\|\|[^<>|]*>", Name = "playerTitle", RestoreFromGlossary = true },
            new() { Literal = "gangName", Name = "gangName", RestoreAs = "///gangName///" },
            new() { Pattern = CodeLetterContext + "i1" + CodeLetterEnd, Name = "randomItemOne", RestoreAs = "///i1///" },
            new() { Pattern = CodeLetterContext + "i" + CodeLetterEnd, Name = "randomItem", RestoreAs = "///i///" },
            new() { Pattern = CodeLetterContext + "a" + CodeLetterEnd, Name = "npcName", RestoreAs = "///a///" },
            .. extra,
        ], Glossary);

    [Fact(DisplayName = "A literal token becomes its slot with a copy value")]
    public void Literal_IsSlotted()
    {
        var (slotted, tokens) = Slotter().Slot("你好，<fName>，欢迎回来");

        Assert.Equal("你好，{firstName}，欢迎回来", slotted);
        Assert.Equal("<fName>", tokens["firstName"].Source);
    }

    [Fact(DisplayName = "Text with no token is returned as it was")]
    public void NoToken_IsUnchanged()
    {
        var (slotted, tokens) = Slotter().Slot("你好，欢迎回来");

        Assert.Equal("你好，欢迎回来", slotted);
        Assert.Empty(tokens);
    }

    [Fact(DisplayName = "A slotter with nothing configured does nothing")]
    public void None_IsInert()
    {
        var (slotted, tokens) = TokenSlotter.None.Slot("你好<fName>");

        Assert.Equal("你好<fName>", slotted);
        Assert.Empty(tokens);
        Assert.Equal("{x}", TokenSlotter.None.Restore("{x}", new Dictionary<string, TokenValue> { ["x"] = new("y") }).Text);
        Assert.Null(TokenSlotter.None.FindSlotProblem("{a}", "b"));
    }

    [Fact(DisplayName = "A fixed token restores to its configured text")]
    public void Fixed_RestoresToItsText()
    {
        var slotter = Slotter();
        var (slotted, tokens) = slotter.Slot("咱们gangName在这");

        Assert.Equal("咱们{gangName}在这", slotted);
        Assert.Equal("gangName", tokens["gangName"].Source);
        Assert.Equal("Our ///gangName/// is here", slotter.Restore("Our {gangName} is here", tokens).Text);
    }

    [Fact(DisplayName = "A glossary token restores to the glossary Result of its source")]
    public void Glossary_RestoresFromTheGlossary()
    {
        var slotter = Slotter();
        var (slotted, tokens) = slotter.Slot($"{OtherTitle}，请留步");

        Assert.Equal("{playerTitle}，请留步", slotted);
        Assert.Equal(OtherTitle, tokens["playerTitle"].Source);
        Assert.Equal("<Young Master||Miss>, wait", slotter.Restore("{playerTitle}, wait", tokens).Text);
    }

    [Fact(DisplayName = "A glossary token with no glossary entry is not slotted")]
    public void Glossary_WithoutEntry_IsLeftAlone()
    {
        var (slotted, tokens) = Slotter().Slot("<某某||某某>你好");

        Assert.Equal("<某某||某某>你好", slotted);
        Assert.Empty(tokens);
    }

    [Fact(DisplayName = "A glossary change reaches the restored text with the slotted text untouched")]
    public void Glossary_ChangeNeedsNoReslot()
    {
        var tokens = new Dictionary<string, TokenValue> { ["playerTitle"] = new(Title) };
        var changed = new TokenSlotter([new NamedTokenReplacer { Pattern = @"<[^<>|]*\|\|[^<>|]*>", Name = "playerTitle", RestoreFromGlossary = true }],
            [new GlossaryLine(Title, "<Hero||Heroine>")]);

        Assert.Equal("<Young Hero||Miss>, hi", Slotter().Restore("{playerTitle}, hi", tokens).Text);
        Assert.Equal("<Hero||Heroine>, hi", changed.Restore("{playerTitle}, hi", tokens).Text);
    }

    [Fact(DisplayName = "The same source reuses its slot and a different one gets a numbered name")]
    public void SameName_DifferentSource_IsSuffixed()
    {
        var slotter = Slotter();
        var (slotted, tokens) = slotter.Slot($"{Title}和{OtherTitle}和{Title}");

        Assert.Equal("{playerTitle}和{playerTitle2}和{playerTitle}", slotted);
        Assert.Equal(Title, tokens["playerTitle"].Source);
        Assert.Equal(OtherTitle, tokens["playerTitle2"].Source);
        Assert.Equal("<Young Hero||Miss> and <Young Master||Miss>", slotter.Restore("{playerTitle} and {playerTitle2}", tokens).Text);
    }

    [Fact(DisplayName = "A shared cell map keeps one name meaning one thing across fragments")]
    public void CellTokens_ShareNumbering()
    {
        var slotter = Slotter();
        var cell = new Dictionary<string, TokenValue>();

        var (first, firstTokens) = slotter.Slot(Title, cell);
        var (second, secondTokens) = slotter.Slot(OtherTitle, cell);
        var (third, thirdTokens) = slotter.Slot(Title, cell);

        Assert.Equal(["{playerTitle}", "{playerTitle2}", "{playerTitle}"], [first, second, third]);
        Assert.Equal(["playerTitle"], firstTokens.Keys);
        Assert.Equal(["playerTitle2"], secondTokens.Keys);
        Assert.Equal(["playerTitle"], thirdTokens.Keys);
        Assert.Equal(2, cell.Count);
    }

    [Fact(DisplayName = "A token ending in a digit is slotted whole and restores to its own text")]
    public void DigitToken_IsSlottedWhole()
    {
        var slotter = Slotter();
        var (slotted, tokens) = slotter.Slot("得到i1和i共3个");

        Assert.Equal("得到{randomItemOne}和{randomItem}共3个", slotted);
        Assert.Equal("Got ///i1/// and ///i/// x3", slotter.Restore("Got {randomItemOne} and {randomItem} x3", tokens).Text);
    }

    [Fact(DisplayName = "A code letter is slotted only next to Chinese text, a tag or punctuation")]
    public void CodeLetter_NeedsContext()
    {
        var slotter = Slotter();

        Assert.Equal("今日之恩，{npcName}来日必报 data", slotter.Slot("今日之恩，a来日必报 data").Slotted);
        Assert.Equal("a word", slotter.Slot("a word").Slotted);
        Assert.Equal("<color=#E64D00>{npcName}</color>", slotter.Slot("<color=#E64D00>a</color>").Slotted);
    }

    [Fact(DisplayName = "A slot restores from its stored source alone, in any case and spacing")]
    public void Restore_IsCaseAndSpaceInsensitive()
    {
        var slotter = Slotter();
        var tokens = new Dictionary<string, TokenValue> { ["firstName"] = new("<fName>") };

        Assert.Equal("Hi <fName>, <fName>.", slotter.Restore("Hi {firstname}, { firstName }.", tokens).Text);
    }

    [Fact(DisplayName = "A word in braces that is not in the value map is not touched")]
    public void Restore_LeavesOtherBraces()
    {
        var slotter = Slotter();
        var tokens = new Dictionary<string, TokenValue> { ["firstName"] = new("<fName>") };

        Assert.Equal("{0} {foo} <fName>", slotter.Restore("{0} {foo} {firstName}", tokens).Text);
    }

    [Fact(DisplayName = "A slot that cannot be resolved is reported and left in place")]
    public void Restore_ReportsUnresolved()
    {
        var slotter = Slotter();
        var tokens = new Dictionary<string, TokenValue>
        {
            ["playerTitle"] = new("<没有||没有>"),   // no glossary entry
            ["mystery"] = new("x"),                  // no configured token under that name
            ["firstName"] = new("<fName>"),
        };

        var result = slotter.Restore("{playerTitle} {mystery} {firstName}", tokens);

        Assert.False(result.Ok);
        Assert.Equal(["playerTitle", "mystery"], result.Unresolved);
        Assert.Equal("{playerTitle} {mystery} <fName>", result.Text);
    }

    [Fact(DisplayName = "Slot then restore gives the game's own form for every kind, in one line")]
    public void RoundTrip()
    {
        var slotter = Slotter();
        const string source = "<fName>，gangName的a说：<少侠||姑娘>，i1和i归你，<公子||姑娘>也是。";

        var (slotted, tokens) = slotter.Slot(source);
        var restored = slotter.Restore(slotted, tokens);

        Assert.Equal("{firstName}，{gangName}的{npcName}说：{playerTitle}，{randomItemOne}和{randomItem}归你，{playerTitle2}也是。", slotted);
        Assert.Equal("<fName>，///gangName///的///a///说：<Young Hero||Miss>，///i1///和///i///归你，<Young Master||Miss>也是。", restored.Text);
        Assert.Equal(source, TokenSlotter.RestoreSource(slotted, tokens));
    }

    [Fact(DisplayName = "A slot next to a numbered slot stays a slot")]
    public void SlotNextToNumberedSlot()
    {
        var slotter = Slotter();
        var (slotted, tokens) = slotter.Slot("{0}<fName>{1}");

        Assert.Equal("{0}{firstName}{1}", slotted);
        Assert.Equal("{0}<fName>{1}", slotter.Restore(slotted, tokens).Text);
        Assert.True(slotter.IsSlotName("firstName"));
        Assert.True(slotter.IsSlotName("playerTitle2"));
        Assert.False(slotter.IsSlotName("playerTitle1"));
        Assert.False(slotter.IsSlotName("0"));
        Assert.False(slotter.IsSlotName("firstNames"));
    }

    [Theory(DisplayName = "A candidate is checked against its slotted source")]
    [InlineData("你好{firstName}", "Hello {firstName}", null)]
    [InlineData("你好{firstName}和{firstName}", "Hello {firstName}", "Missing required token '{firstName}'.")]
    [InlineData("你好{firstName}", "Hello", "Missing required token '{firstName}'.")]
    [InlineData("你好{firstName}", "Hello { FirstName }", null)]
    [InlineData("你好{firstName}", "Hello {firstName} {npcName}", "Invented slot '{npcName}' that the source does not have.")]
    [InlineData("你好{firstName}", "Hello {firstName} {he}", "Unknown placeholder '{he}' that the source does not have.")]
    [InlineData("你好{foo}{firstName}", "Hello {foo} {firstName}", null)]
    [InlineData("你好{0}{firstName}", "Hello {0}{firstName}", null)]
    public void FindSlotProblem_Reports(string source, string candidate, string? expected) =>
        Assert.Equal(expected, Slotter().FindSlotProblem(source, candidate));

    [Fact(DisplayName = "A leftover slot is found in a restored text")]
    public void FindUnrestoredSlot_FindsConfiguredSlots()
    {
        var slotter = Slotter();

        Assert.Equal("{npcName}", slotter.FindUnrestoredSlot("Hi {npcName}, {foo}"));
        Assert.Null(slotter.FindUnrestoredSlot("Hi {foo} {0}"));
    }

    [Fact(DisplayName = "A translation written in restored form is put back into slots")]
    public void MaskRestored_ReslotsATranslation()
    {
        var slotter = Slotter();
        var (_, tokens) = slotter.Slot($"{Title}，<fName>，gangName");

        Assert.Equal("{playerTitle}, {firstName}, {gangName}", slotter.MaskRestored("<Young Hero||Miss>, <fName>, ///gangName///", tokens));
    }

    [Fact(DisplayName = "The prompt describes each slot once, however many names it has")]
    public void Describe_ListsPresentSlots()
    {
        var slotter = Slotter();

        Assert.Contains("player's first name", slotter.Describe("{firstName}，你好"));
        Assert.Equal(string.Empty, slotter.Describe("你好"));
        Assert.Single(slotter.Describe("{firstName}{firstName}").Split('\n'));
    }

    [Theory(DisplayName = "A bad entry fails at configuration time")]
    [InlineData("1bad", "<x>", "")]
    [InlineData("name", "", "")]
    [InlineData("name", "<x>", "<x+>")]
    public void BadEntry_Throws(string name, string literal, string pattern) =>
        Assert.Throws<InvalidOperationException>(() => new TokenSlotter([new NamedTokenReplacer { Name = name, Literal = literal, Pattern = pattern }], []));

    [Fact(DisplayName = "Two slotters hold nothing in common")]
    public void Slotters_AreIndependent()
    {
        var one = new TokenSlotter([new NamedTokenReplacer { Literal = "<x>", Name = "x" }], []);
        var other = new TokenSlotter([new NamedTokenReplacer { Literal = "<x>", Name = "y" }], []);

        Assert.Equal("{x}", one.Slot("<x>").Slotted);
        Assert.Equal("{y}", other.Slot("<x>").Slotted);
        Assert.Equal("{x}", one.Slot("<x>").Slotted);
    }
}
