using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace Tests;

/// <summary>Hyphens stay ordinary "-" when saving; translations saved with U+2011 earlier still validate.</summary>
public class LineValidationHyphenTests
{
    private static TextFileToSplit TextFile() => new() { Path = "Test.txt", TextFileType = TextFileType.RawCsv };

    [Theory(DisplayName = "Saving keeps an ordinary hyphen and turns a model-emitted U+2011 into one")]
    [InlineData("Half-dead", "Half-dead")]
    [InlineData("Half\u2011dead", "Half-dead")]
    [InlineData("-5 Attack", "-5 Attack")]
    [InlineData("{-1}", "{-1}")]
    public void CleanupLineBeforeSaving_UsesOrdinaryHyphens(string input, string expected)
    {
        var saved = LineValidation.CleanupLineBeforeSaving(input, "原文", TextFile(), new StringTokenReplacer());

        Assert.Equal(expected, saved);
    }

    [Fact(DisplayName = "A glossary result with a hyphen is found in a translation saved with U+2011")]
    public void GlossaryCheck_AcceptsLegacyNonBreakingHyphen()
    {
        var config = new LlmConfig();
        config.Runtime.GlossaryLines.Add(new GlossaryLine { Raw = "半死", Result = "half-dead" });

        var missing = TranslationWorkflow.FindGlossaryMistranslations(config, "他半死不活", "He is half‑dead", TextFile()).ToList();

        Assert.Empty(missing);
    }
}
