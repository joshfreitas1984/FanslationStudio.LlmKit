using System.Reflection;
using System.Text.RegularExpressions;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public class QualityReviewWorkflowTests
{
    // Regression test for a real corrupted QcTranslated found in DragonHierOverLlm's
    // AchievementData.csv.yaml: "Wealth in the millions CORRECTED: NONE" and "Become the number one
    // ... CORRECTED: Become the top fighter ...". CorrectedLineRegex used to be Singleline with no
    // line anchor, so it captured everything from the first "CORRECTED:" to the end of the whole
    // response instead of just that one line - if the model's response restated the label or added
    // any trailing text after it, that trailing text got swept into the captured correction.
    [Theory(DisplayName = "CorrectedLineRegex captures only the CORRECTED line, not trailing text")]
    [InlineData("SCORE: 85\nCORRECTED: NONE", "NONE")]
    [InlineData("SCORE: 85\nCORRECTED: Wealth in the millions\nCORRECTED: NONE", "Wealth in the millions")]
    [InlineData("SCORE: 90\nCORRECTED: Become the top fighter in the heroes' battle rankings", "Become the top fighter in the heroes' battle rankings")]
    public void CorrectedLineRegexStopsAtEndOfLine(string llmResponse, string expectedCorrected)
    {
        var regex = (Regex)typeof(QualityReviewWorkflow)
            .GetField("CorrectedLineRegex", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        var match = regex.Match(llmResponse);

        Assert.True(match.Success);
        Assert.Equal(expectedCorrected, match.Groups[1].Value.Trim());
    }

    // Regression test for a real bad correction found in DragonHierOverLlm's ArmorData.csv.yaml:
    // "Full helmet" -> "FULL HELMET", accepted with QcStatus.Corrected and a self-reported
    // QcQualityScore of 100. CheckCapitalizationRegression is the gate that must now reject it.
    [Fact(DisplayName = "Rejects a correction that shouts in all-caps over a normally-cased original")]
    public void RejectsAllCapsShoutOverNormalCasing()
    {
        var reason = QualityReviewWorkflow.CheckCapitalizationRegression("Full helmet", "FULL HELMET");

        Assert.NotNull(reason);
    }

    [Theory(DisplayName = "Allows corrections that don't introduce an all-caps shout")]
    [InlineData("Full helmet", "Full helm")] // ordinary wording correction
    [InlineData("UI", "UI")] // already all-caps, unchanged
    [InlineData("HP", "MP")] // already all-caps both sides (e.g. genuine acronym)
    [InlineData("I", "I")] // single uppercase letter, no lowercase to regress from
    public void AllowsNonRegressingCorrections(string original, string corrected)
    {
        var reason = QualityReviewWorkflow.CheckCapitalizationRegression(original, corrected);

        Assert.Null(reason);
    }

    // Regression test for a real corrupted QcTranslated found in DragonHierOverLlm's
    // KungFuData.csv.yaml: "Sword Technique Power NONE" - the model appended the "NONE" sentinel
    // onto real corrected text instead of using it as the whole response. The old guard only caught
    // a "CORRECTED:" label leak and an exact-match "NONE"; it let this one through because
    // correctedRaw wasn't literally just "NONE". ContainsLeakedProtocolText is the generalized check
    // that must catch this, plus every other protocol label the QC prompt shows the model.
    [Theory(DisplayName = "ContainsLeakedProtocolText catches every QC-protocol leak, not just a bare label")]
    [InlineData("Sword Technique Power NONE", true)] // NONE stuck onto real text
    [InlineData("Defeat more than 10 enemies in a single battle with your own handsNONE", true)] // glued with no separator at all (real AchievementData.csv.yaml case)
    [InlineData("NONE Sword Technique Power", true)] // leading instead of trailing
    [InlineData("Wealth in the millions CORRECTED: NONE", true)] // the original documented leak
    [InlineData("Become the top fighter SCORE: 90", true)] // a different label leaking
    [InlineData("SOURCE (Chinese): 剑法威力", true)] // model echoing the input label
    [InlineData("CURRENT TRANSLATION (English): Sword Technique Power", true)] // ditto, other label
    [InlineData("Nonetheless, it works", false)] // "None" as a word-fragment prefix, not standalone
    [InlineData("Sword Technique Power", false)] // clean correction, no leak
    [InlineData("NONE", false)] // the legitimate "no correction needed" sentinel
    [InlineData("none", false)] // sentinel, case-insensitive
    public void ContainsLeakedProtocolTextDetectsLeaks(string correctedText, bool expectedLeak)
    {
        Assert.Equal(expectedLeak, QualityReviewWorkflow.ContainsLeakedProtocolText(correctedText));
    }
}
