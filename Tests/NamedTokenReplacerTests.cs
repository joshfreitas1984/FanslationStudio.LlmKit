using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace Tests;

/// <summary>
/// <see cref="StringTokenReplacer"/> with <see cref="NamedTokenReplacer"/> entries configured. Converted holds slotted text
/// (<c>{firstName}</c>, see <see cref="TokenSlotter"/>), so the replacer's own job is to leave a slot alone while it masks everything
/// else under numbered <c>{n}</c> slots, and to describe the slots to the model. Slotting and restoring are covered in
/// <see cref="TokenSlotterTests"/>.
/// </summary>
[Collection("StringTokenReplacerStatics")]
public class NamedTokenReplacerTests : IDisposable
{
    private const string Title = "<少侠||姑娘>";
    private const string OtherTitle = "<公子||姑娘>";

    private static readonly List<GlossaryLine> Glossary =
    [
        new(Title, "<Young Hero||Miss>"),
        new(OtherTitle, "<Young Master||Miss>"),
    ];

    public NamedTokenReplacerTests() => Configure();

    public void Dispose() => StringTokenReplacer.SetNamedTokens([], []);

    private static void Configure(params NamedTokenReplacer[] extra)
    {
        List<NamedTokenReplacer> entries =
        [
            new() { Literal = "<fName>", Name = "firstName", Description = "the player's first name." },
            new() { Pattern = @"<[^<>|]*\|\|[^<>|]*>", Name = "playerTitle", RestoreFromGlossary = true, Description = "how someone addresses the player, whose gender is unknown." },
            .. extra,
        ];
        StringTokenReplacer.SetNamedTokens(entries, Glossary);
    }

    [Fact(DisplayName = "A slot in slotted text is left as it is")]
    public void Slot_IsLeftAlone()
    {
        var replacer = new StringTokenReplacer();
        var masked = replacer.Replace("你好，{firstName}，欢迎回来");

        Assert.Equal("你好，{firstName}，欢迎回来", masked);
        Assert.Equal("Hello, {firstName}.", replacer.Restore("Hello, {firstName}."));
    }

    [Fact(DisplayName = "The replacer no longer turns the game's tokens into slots")]
    public void GameToken_IsNotSlottedHere()
    {
        Assert.Equal("你好，<fName>", new StringTokenReplacer().Replace("你好，<fName>"));
    }

    [Fact(DisplayName = "A numbered slot and a named slot in the same line do not collide")]
    public void NumberedSlotAndNamedSlot_DoNotCollide()
    {
        var replacer = new StringTokenReplacer();
        var masked = replacer.Replace("{firstName}获得了{item}");

        Assert.Equal("{firstName}获得了{0}", masked);
        Assert.Equal("{firstName} got {item}", replacer.Restore("{firstName} got {0}"));
    }

    [Fact(DisplayName = "A suffixed slot name keeps its digit away from the numbered pass")]
    public void SuffixedSlot_IsNotRenumbered()
    {
        var replacer = new StringTokenReplacer();
        var masked = replacer.Replace("{playerTitle}和{playerTitle2}，5个");

        Assert.Equal("{playerTitle}和{playerTitle2}，{0}个", masked);
    }

    [Fact(DisplayName = "A two digit suffix is not split into a number")]
    public void TwoDigitSuffix_IsNotSplit()
    {
        var replacer = new StringTokenReplacer();

        Assert.Equal("{playerTitle10}你好", replacer.Replace("{playerTitle10}你好"));
    }

    [Fact(DisplayName = "A slot next to a number leaves the number numbered")]
    public void SlotNextToNumber()
    {
        var replacer = new StringTokenReplacer();
        var masked = replacer.Replace("{firstName}3次");

        Assert.Equal("{firstName}{0}次", masked);
        Assert.Equal("{firstName}3 times", replacer.Restore("{firstName}{0} times"));
    }

    [Fact(DisplayName = "A word in braces that is not a slot is still masked as a numbered slot")]
    public void OtherBraces_AreStillNumbered()
    {
        var replacer = new StringTokenReplacer();

        Assert.Equal("{0}{firstName}", replacer.Replace("{foo}{firstName}"));
        Assert.Equal("{foo}{firstName}", replacer.Restore("{0}{firstName}"));
    }

    [Fact(DisplayName = "The prompt describes only the named slots present")]
    public void DescribeNamedTokensIn_ListsPresentTokens()
    {
        var described = StringTokenReplacer.DescribeNamedTokensIn("{firstName}，你好");

        Assert.Contains("`{firstName}`", described);
        Assert.DoesNotContain("playerTitle", described);
        Assert.Equal(string.Empty, StringTokenReplacer.DescribeNamedTokensIn("你好"));
        Assert.Contains("`{playerTitle}`", StringTokenReplacer.DescribeNamedTokensIn("{playerTitle2}，你好"));
    }

    [Fact(DisplayName = "With no tokens configured a slot-looking word is numbered like any other")]
    public void NoTokensConfigured_NumbersEverything()
    {
        StringTokenReplacer.SetNamedTokens([], []);

        Assert.Equal("{0}你好", new StringTokenReplacer().Replace("{firstName}你好"));
    }

    [Theory(DisplayName = "A bad entry fails at configuration time")]
    [InlineData("1bad", "<x>", "")]
    [InlineData("name", "", "")]
    [InlineData("name", "<x>", "<x+>")]
    public void BadEntry_Throws(string name, string literal, string pattern) =>
        Assert.Throws<InvalidOperationException>(() =>
            StringTokenReplacer.SetNamedTokens([new NamedTokenReplacer { Name = name, Literal = literal, Pattern = pattern }], []));
}

[Collection("StringTokenReplacerStatics")]
public class NamedTokenReplacerConfigTests
{
    [Fact(DisplayName = "restoreFromGlossary and restoreAs cannot be combined")]
    public void RestoreFromGlossaryAndRestoreAs_Throws() =>
        Assert.Throws<InvalidOperationException>(() => StringTokenReplacer.SetNamedTokens(
            [new NamedTokenReplacer { Name = "x", Literal = "<x>", RestoreFromGlossary = true, RestoreAs = "y" }], []));
}

/// <summary>StringTokenReplacer keeps its token configuration in statics that any config load resets, so these tests run with nothing else in parallel.</summary>
[CollectionDefinition("StringTokenReplacerStatics", DisableParallelization = true)]
public class StringTokenReplacerStaticsCollection;
