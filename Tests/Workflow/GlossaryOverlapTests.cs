using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

// A glossary term that only appears as part of a longer matched term (三七 "Sanqi" inside the idiom
// 三七开 "70/30 split") must not be injected into the prompt or demanded by the rule check.
public class GlossaryOverlapTests
{
    private static readonly GlossaryLine Herb = new("三七", "Sanqi");
    private static readonly GlossaryLine Split = new("三七开", "70/30 split");

    private static LlmConfig ConfigWith(params GlossaryLine[] lines)
    {
        var config = new LlmConfig();
        config.Runtime.GlossaryLines = [.. lines];
        return config;
    }

    private static readonly TextFileToSplit File = new() { Path = "A.txt", EnableGlossary = true };

    [Fact(DisplayName = "AppendPromptsFor drops a term that only occurs inside a longer matched term")]
    public void Prompt_DropsShadowedTerm()
    {
        var prompt = GlossaryLine.AppendPromptsFor("哼，就三七开吧！", [Herb, Split], "A.txt");

        Assert.Contains("三七开", prompt);
        Assert.DoesNotContain("Sanqi", prompt);
    }

    [Fact(DisplayName = "AppendPromptsFor keeps a short term when no longer term matched")]
    public void Prompt_KeepsShortTermWithoutLongerMatch()
    {
        var prompt = GlossaryLine.AppendPromptsFor("三七是一味药材", [Herb, Split], "A.txt");

        Assert.Contains("Sanqi", prompt);
        Assert.DoesNotContain("70/30", prompt);
    }

    [Fact(DisplayName = "AppendPromptsFor keeps a short term that also occurs on its own")]
    public void Prompt_KeepsShortTermOccurringStandalone()
    {
        var prompt = GlossaryLine.AppendPromptsFor("就三七开吧，再加一钱三七", [Herb, Split], "A.txt");

        Assert.Contains("Sanqi", prompt);
        Assert.Contains("70/30 split", prompt);
    }

    [Fact(DisplayName = "A longer term scoped to another file does not shadow")]
    public void Prompt_LongerTermForOtherFileDoesNotShadow()
    {
        var scoped = new GlossaryLine("三七开", "70/30 split") { OnlyOutputFiles = ["B.txt"] };

        var prompt = GlossaryLine.AppendPromptsFor("哼，就三七开吧！", [Herb, scoped], "A.txt");

        Assert.Contains("Sanqi", prompt);
    }

    [Fact(DisplayName = "FindGlossaryMistranslations does not demand a shadowed term")]
    public void RuleCheck_DoesNotDemandShadowedTerm()
    {
        var config = ConfigWith(Herb, Split);

        var missing = TranslationWorkflow.FindGlossaryMistranslations(config, "哼，就三七开吧！", "Hmph, let's go 70/30 split!", File).ToList();

        Assert.Empty(missing);
    }

    [Fact(DisplayName = "FindGlossaryMistranslations still demands the longer term")]
    public void RuleCheck_StillDemandsLongerTerm()
    {
        var config = ConfigWith(Herb, Split);

        var missing = TranslationWorkflow.FindGlossaryMistranslations(config, "哼，就三七开吧！", "Hmph, let's go fifty-fifty!", File).ToList();

        Assert.Equal(["三七开"], missing.Select(x => x.Raw));
    }

    [Fact(DisplayName = "FindGlossaryMistranslations still demands a standalone short term")]
    public void RuleCheck_StillDemandsStandaloneShortTerm()
    {
        var config = ConfigWith(Herb, Split);

        var missing = TranslationWorkflow.FindGlossaryMistranslations(config, "就三七开吧，再加一钱三七", "Let's go 70/30 split, add a pinch of herb.", File).ToList();

        Assert.Equal(["三七"], missing.Select(x => x.Raw));
    }
}
