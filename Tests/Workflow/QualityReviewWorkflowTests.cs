using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public class QualityReviewWorkflowTests
{
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
}
