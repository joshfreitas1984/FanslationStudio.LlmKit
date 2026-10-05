using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;

namespace Tests;

public class LineContextQcTests
{
    private static TranslationLine Line(string text) => new()
    {
        Raw = text,
        Splits = [new TranslationSplit { Text = text, Translated = "x" }],
    };

    [Fact(DisplayName = "QC user prompt is unchanged when there is no line context")]
    public void NoContext_PromptUnchanged()
    {
        var prompt = QualityReviewWorkflow.BuildQcUserPrompt("源", "src", "术语=Term");

        Assert.DoesNotContain("Line context", prompt);
        Assert.Contains("Relevant glossary terms", prompt);
        Assert.Contains("术语=Term", prompt);
    }

    [Fact(DisplayName = "QC user prompt renders the line context as its own block, separate from the glossary")]
    public void WithContext_RendersSeparateBlock()
    {
        var combined = "术语=Term" + LineContexts.QcSeparator + "The speaker is a female character.";

        var prompt = QualityReviewWorkflow.BuildQcUserPrompt("源", "src", combined);

        Assert.Contains("Line context from the game", prompt);
        Assert.Contains("The speaker is a female character.", prompt);
        Assert.Contains("术语=Term", prompt);
        // Neither block leaks the separator or the other block's text.
        Assert.DoesNotContain("LINE-CONTEXT", prompt);
        var glossaryStart = prompt.IndexOf("Relevant glossary terms", StringComparison.Ordinal);
        Assert.DoesNotContain("female character", prompt[glossaryStart..]);
    }

    [Fact(DisplayName = "QC user prompt renders a line context even when there are no glossary terms")]
    public void ContextWithoutGlossary_StillRenders()
    {
        var prompt = QualityReviewWorkflow.BuildQcUserPrompt("源", "src", LineContexts.QcSeparator + "Narration addressed to the player.");

        Assert.Contains("Narration addressed to the player.", prompt);
        Assert.DoesNotContain("Relevant glossary terms", prompt);
    }

    [Fact(DisplayName = "WithColumnContext adds the anchor's context only when it has one")]
    public void WithColumnContext_OnlyWhenPresent()
    {
        var config = new LlmConfig();
        var withContext = Line("（笑了笑）").Splits[0];
        var without = Line("你好").Splits[0];
        config.Runtime.LineContexts[withContext] = new LineContext("male speaker", true, LineContext.Male);

        Assert.Equal("glossary", LineContexts.WithColumnContext(config, "glossary", without));
        Assert.Equal("glossary" + LineContexts.QcSeparator + "male speaker", LineContexts.WithColumnContext(config, "glossary", withContext));
    }

    [Fact(DisplayName = "LineContexts.Build builds nothing unless lineContextEnabled and a provider is set")]
    public void Build_RespectsFlagAndProvider()
    {
        var line = Line("（笑了笑）");
        var textFile = new TextFileToSplit { Path = "Test.txt" };
        var hooks = new GameHooks
        {
            LineContextProvider = (_, _, lines) => new Dictionary<TranslationSplit, LineContext>
            {
                [lines[0].Splits[0]] = new LineContext("hint", false),
            },
        };
        var files = new[] { (textFile, (IReadOnlyList<TranslationLine>)new List<TranslationLine> { line }) };

        var disabled = new LlmConfig { LineContextEnabled = false, Hooks = hooks };
        LineContexts.Build(disabled, "wd", files);
        Assert.Empty(disabled.Runtime.LineContexts);

        var noProvider = new LlmConfig { LineContextEnabled = true };
        LineContexts.Build(noProvider, "wd", files);
        Assert.Empty(noProvider.Runtime.LineContexts);

        var enabled = new LlmConfig { LineContextEnabled = true, Hooks = hooks };
        LineContexts.Build(enabled, "wd", files);
        Assert.Equal("hint", enabled.Runtime.LineContexts[line.Splits[0]].Prompt);
    }
}
