using System.Reflection;
using System.Text.RegularExpressions;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public class QualityReviewWorkflowTests
{
    // CorrectedLineRegex is deliberately Singleline (dot matches newline) as of commit 116539a - a
    // multi-sentence correction is frequently joined by a real line break rather than a literal
    // "\n" token, and an earlier single-line-anchored version of this regex silently truncated
    // those (see the regex's own doc comment). That means a plain, un-leaked correction spanning
    // multiple real lines is captured whole, as this case verifies.
    [Fact(DisplayName = "CorrectedLineRegex captures a plain multi-line correction in full")]
    public void CorrectedLineRegexCapturesMultiLineCorrection()
    {
        var regex = GetCorrectedLineRegex();

        var match = regex.Match("SCORE: 85\nCORRECTED: First sentence.\nSecond sentence on its own line.");

        Assert.True(match.Success);
        Assert.Equal("First sentence.\nSecond sentence on its own line.", match.Groups[1].Value.Trim());
    }

    [Theory(DisplayName = "CorrectedLineRegex captures a single-line correction exactly")]
    [InlineData("SCORE: 85\nCORRECTED: NONE", "NONE")]
    [InlineData("SCORE: 90\nCORRECTED: Become the top fighter in the heroes' battle rankings", "Become the top fighter in the heroes' battle rankings")]
    public void CorrectedLineRegexCapturesSingleLineCorrection(string llmResponse, string expectedCorrected)
    {
        var match = GetCorrectedLineRegex().Match(llmResponse);

        Assert.True(match.Success);
        Assert.Equal(expectedCorrected, match.Groups[1].Value.Trim());
    }

    // Regression test for a real corrupted QcTranslated found in DragonHierOverLlm's
    // AchievementData.csv.yaml: "Wealth in the millions CORRECTED: NONE". Because CorrectedLineRegex
    // is Singleline (see above), a model that restates "CORRECTED:"/"SCORE:" on a later line gets
    // that whole trailing leak swept into the captured group rather than truncated - this is by
    // design, NOT a bug: the regex's own doc comment calls out ContainsLeakedProtocolText as the
    // independent guard for exactly this case. This test locks in that two-stage contract: the
    // regex captures everything (including the leak), and ContainsLeakedProtocolText then flags
    // that captured text so GetLlmVerdictAsync discards the whole response as unparseable instead
    // of quietly accepting the leading, seemingly-clean-looking prefix.
    [Fact(DisplayName = "CorrectedLineRegex captures a trailing leaked CORRECTED: line, which ContainsLeakedProtocolText then flags")]
    public void CorrectedLineRegexCapturesLeakWhichIsThenFlagged()
    {
        var match = GetCorrectedLineRegex().Match("SCORE: 85\nCORRECTED: Wealth in the millions\nCORRECTED: NONE");

        Assert.True(match.Success);
        var captured = match.Groups[1].Value.Trim();
        Assert.Equal("Wealth in the millions\nCORRECTED: NONE", captured);
        Assert.True(QualityReviewWorkflow.ContainsLeakedProtocolText(captured));
    }

    private static Regex GetCorrectedLineRegex() =>
        (Regex)typeof(QualityReviewWorkflow)
            .GetField("CorrectedLineRegex", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

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
    // Regression test for a false positive found in real QC output: the standalone/trailing "NONE"
    // checks used to be case-insensitive, so an ordinary lowercase "none" inside genuine, correct
    // English prose got misidentified as the leaked protocol sentinel and the whole response was
    // discarded as unparseable.
    [InlineData("When the Buddha was first born, he roared like a lion, declaring that in heaven and on earth, none but I am supreme.", false)]
    public void ContainsLeakedProtocolTextDetectsLeaks(string correctedText, bool expectedLeak)
    {
        Assert.Equal(expectedLeak, QualityReviewWorkflow.ContainsLeakedProtocolText(correctedText));
    }
}
