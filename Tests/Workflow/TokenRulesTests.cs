using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

/// <summary>
/// The rules pass reads slotted text (<c>{firstName}</c>): a slot the translation dropped, invented or renamed is a failure in slot
/// space, while a game's own validator and the pronoun checks look at the game's tokens (<c>&lt;fName&gt;</c>), so they are handed the
/// restored forms. A manual translation is looked up by the original text and re-slotted.
/// </summary>
[Collection("StringTokenReplacerStatics")]
public class TokenRulesTests : IDisposable
{
    private static readonly TextFileToSplit File1 = new() { Path = "Dialog.csv" };

    public TokenRulesTests() => StringTokenReplacer.SetNamedTokens(
    [
        new NamedTokenReplacer { Literal = "<fName>", Name = "firstName" },
        new NamedTokenReplacer { Literal = "a", Name = "npcName", RestoreAs = "///a///" },
    ], []);

    public void Dispose() => StringTokenReplacer.SetNamedTokens([], []);

    private static TranslationSplit Split(string text, string translated) => new()
    {
        Split = 1,
        Text = text,
        Translated = translated,
        Tokens = new() { ["firstName"] = new TokenValue("<fName>"), ["npcName"] = new TokenValue("a") },
    };

    private static LlmConfig Config(GameHooks? hooks = null)
    {
        var config = new LlmConfig { Hooks = hooks ?? new GameHooks() };
        config.Runtime.Models["Default"] = ConfigurationExtensions.GetPresetModelConfig(ModelPreset.Qwen25, ModelPresetType.Standard);
        return config;
    }

    private static (TranslationSplit Split, ConcurrentBag<string> Log) Run(TranslationSplit split, LlmConfig config)
    {
        var log = new ConcurrentBag<string>();
        TranslationWorkflow.UpdateSplit(log, new TranslationLine { Raw = split.Text, Splits = [split] }, split, File1, config,
            new Regex(LineValidation.ChineseCharPattern), new StringTokenReplacer());
        return (split, log);
    }

    [Fact(DisplayName = "A translation that keeps its slots passes")]
    public void KeptSlots_Pass()
    {
        var (split, _) = Run(Split("{firstName}兄，你好", "Brother {firstName}, hello"), Config());

        Assert.False(split.FlaggedForRetranslation);
    }

    [Fact(DisplayName = "A dropped slot is flagged")]
    public void DroppedSlot_IsFlagged()
    {
        var (split, _) = Run(Split("{firstName}兄，你好", "Brother, hello"), Config());

        Assert.True(split.FlaggedForRetranslation);
        Assert.Contains("Missing required token '{firstName}'", split.FlaggedMistranslation);
    }

    [Fact(DisplayName = "An invented slot is flagged")]
    public void InventedSlot_IsFlagged()
    {
        var (split, _) = Run(Split("{firstName}兄，你好", "Brother {firstName} {npcName}, hello"), Config());

        Assert.True(split.FlaggedForRetranslation);
        Assert.Contains("Invented slot '{npcName}'", split.FlaggedMistranslation);
    }

    [Fact(DisplayName = "A renamed slot is flagged as unknown")]
    public void RenamedSlot_IsFlagged()
    {
        var (split, _) = Run(Split("{firstName}兄，你好", "Brother {name}, hello"), Config());

        Assert.True(split.FlaggedForRetranslation);
        Assert.Contains("Missing required token '{firstName}'", split.FlaggedMistranslation);
    }

    [Fact(DisplayName = "A game validator is handed the restored source and translation")]
    public void Validator_SeesTheGameForm()
    {
        var seen = new List<(string Raw, string Result)>();
        var hooks = new GameHooks { CustomColumnValidator = (_, _, raw, result) => { seen.Add((raw, result)); return null; } };

        Run(Split("{firstName}兄，{npcName}来", "Brother {firstName}, {npcName} comes"), Config(hooks));

        Assert.All(seen, s => Assert.Equal(("<fName>兄，///a///来", "Brother <fName>, ///a/// comes"), s));
        Assert.NotEmpty(seen);
    }

    [Fact(DisplayName = "A game validator can still reject a restored translation")]
    public void Validator_CanReject()
    {
        var hooks = new GameHooks { CustomColumnValidator = (_, _, _, result) => result.Contains("///a///") ? null : "needs the NPC token" };

        var (split, _) = Run(Split("{npcName}来", "He comes"), Config(hooks));

        Assert.True(split.FlaggedForRetranslation);
    }

    [Fact(DisplayName = "A manual translation is found by the original text and re-slotted")]
    public void ManualTranslation_IsReslotted()
    {
        var config = Config();
        config.Runtime.ManualTranslations = [new GlossaryLine("<fName>兄，你好", "Brother <fName>, hello")];

        var (split, log) = Run(Split("{firstName}兄，你好", ""), config);

        Assert.Equal("Brother {firstName}, hello", split.Translated);
        Assert.Contains(log, l => l.StartsWith("Manually Translated"));
    }

    [Fact(DisplayName = "A manual translation that leaves a slot out is skipped, not applied")]
    public void ManualTranslation_MissingToken_IsSkipped()
    {
        var config = Config();
        config.Runtime.ManualTranslations = [new GlossaryLine("<fName>兄，你好", "Brother, hello")];

        var (split, log) = Run(Split("{firstName}兄，你好", ""), config);

        Assert.NotEqual("Brother, hello", split.Translated);
        Assert.Contains(log, l => l.StartsWith("Manual translation skipped"));
    }

    [Fact(DisplayName = "The pronoun check looks for the game's tokens in the restored text")]
    public void PronounCheck_UsesTheGameForm()
    {
        var hooks = new GameHooks { UnknownGenderPersonTokens = ["<fName>"] };

        var invented = Split("{firstName}笑了笑，把银两收起来", "{firstName} smiled and put the silver away, he nodded");
        var clean = Split("{firstName}笑了笑，把银两收起来", "{firstName} smiled and put the silver away");

        Assert.Equal("InventedGender", PronounDefectWorkflow.Classify(invented, hooks, null, false).Category);
        Assert.Null(PronounDefectWorkflow.Classify(clean, hooks, null, false).Category);
    }
}
