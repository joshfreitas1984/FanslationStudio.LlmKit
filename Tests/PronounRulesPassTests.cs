using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;
using System.Collections.Concurrent;

namespace Tests;

public class PronounRulesPassTests
{
    private static TranslationSplit Split(string text, string translated) => new() { Text = text, Translated = translated };

    private static TextFileToSplit File() => new() { Path = "Test.txt", TextFileType = TextFileType.RawCsv };

    private static TranslationWorkflow.PronounRulesState State(Dictionary<TranslationSplit, LineContext>? contexts = null) =>
        new(contexts ?? new Dictionary<TranslationSplit, LineContext>());

    [Fact(DisplayName = "The rules pass flags an invented gender for retranslation, every time a stuck line is checked")]
    public void FlagsEveryTime()
    {
        var split = Split("（笑着把银两收起来）", "(Smiling, he put the silver away)");
        var state = State();
        var config = new LlmConfig();
        var log = new ConcurrentBag<string>();

        Assert.True(TranslationWorkflow.TryFlagPronounDefect(log, split, File(), config, state));
        Assert.True(split.FlaggedForRetranslation);
        Assert.Equal("InventedGender", split.FlaggedMistranslation);
        Assert.Single(log);

        // The brute-force loop retranslates and re-checks. The model produced the same defect again, so it is flagged
        // again: a stuck line must stay visible, not be silently accepted after one retry.
        split.FlaggedForRetranslation = false;
        Assert.True(TranslationWorkflow.TryFlagPronounDefect(log, split, File(), config, state));
        Assert.True(split.FlaggedForRetranslation);
        Assert.Equal(2, log.Count);
    }

    [Fact(DisplayName = "The rules pass pronoun check does nothing without state (disabled), for a flagged split, or for a clean translation")]
    public void NoOpCases()
    {
        var config = new LlmConfig();
        var log = new ConcurrentBag<string>();

        var bad = Split("（笑着把银两收起来）", "(Smiling, he put the silver away)");
        Assert.False(TranslationWorkflow.TryFlagPronounDefect(log, bad, File(), config, null));
        Assert.False(bad.FlaggedForRetranslation);

        var alreadyFlagged = Split("（笑着把银两收起来）", "(Smiling, he put the silver away)");
        alreadyFlagged.FlaggedForRetranslation = true;
        Assert.False(TranslationWorkflow.TryFlagPronounDefect(log, alreadyFlagged, File(), config, State()));

        var clean = Split("（笑着把银两收起来）", "(Smiling, put the silver away)");
        Assert.False(TranslationWorkflow.TryFlagPronounDefect(log, clean, File(), config, State()));
        Assert.False(clean.FlaggedForRetranslation);
    }

    [Fact(DisplayName = "The rules pass never flags a source listed in pronounCheck.ignoreSources")]
    public void IgnoredSource_IsNotFlagged()
    {
        var split = Split("（笑着把银两收起来）", "(Smiling, he put the silver away)");
        var config = new LlmConfig();
        config.PronounCheck.IgnoreSources.Add("（笑着把银两收起来）");

        Assert.False(TranslationWorkflow.TryFlagPronounDefect(new ConcurrentBag<string>(), split, File(), config, State()));
        Assert.False(split.FlaggedForRetranslation);
    }

    [Fact(DisplayName = "The rules pass uses a known speaker gender: the right pronoun passes, the wrong one is flagged")]
    public void UsesLineContext()
    {
        var right = Split("（点了点头）", "(Smiled, she nodded)");
        var wrong = Split("（点了点头）", "(Smiled, he nodded)");
        var female = new LineContext("female", true, LineContext.Female);
        var state = State(contexts: new Dictionary<TranslationSplit, LineContext> { [right] = female, [wrong] = female });
        var log = new ConcurrentBag<string>();

        Assert.False(TranslationWorkflow.TryFlagPronounDefect(log, right, File(), new LlmConfig(), state));
        Assert.True(TranslationWorkflow.TryFlagPronounDefect(log, wrong, File(), new LlmConfig(), state));
        Assert.Equal("WrongGender", wrong.FlaggedMistranslation);
    }

    [Fact(DisplayName = "The rules pass flags a he/his next to a game's unknown-gender person token")]
    public void FlagsPlayerToken()
    {
        var split = Split("#PlayerName#手脚挺快，", "#PlayerName# is quick on his feet");
        var withTokens = new LlmConfig { Hooks = new GameHooks { UnknownGenderPersonTokens = ["#PlayerName#"] } };

        Assert.False(TranslationWorkflow.TryFlagPronounDefect(new ConcurrentBag<string>(), split, File(), new LlmConfig(), State()));
        Assert.True(TranslationWorkflow.TryFlagPronounDefect(new ConcurrentBag<string>(), split, File(), withTokens, State()));
    }

    [Fact(DisplayName = "pronounCheck defaults to on and reads skipWhenTranslationNamesSomeone")]
    public void ConfigDefaults()
    {
        var config = new LlmConfig();

        Assert.True(config.PronounCheck.Enabled);
        Assert.False(config.PronounCheck.SkipWhenTranslationNamesSomeone);
        Assert.True(PronounDefectWorkflow.PronounDefectOptions.From(new PronounCheckConfig { SkipWhenTranslationNamesSomeone = true }).SkipWhenTranslationNamesSomeone);
    }
}
