using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace Tests.Utility;

public class QualityReviewHelpersTests
{
    private static readonly QualityReviewConfig EnabledConfig = new() { Enabled = true };

    [Theory(DisplayName = "IsCorrectedLabelLeak flags a QcTranslated still carrying a CORRECTED: label")]
    [InlineData("Wealth in the millions CORRECTED: NONE", true)]
    [InlineData("Become the number one in the heroes' battle rankings CORRECTED: Become the top fighter in the heroes' battle rankings", true)]
    [InlineData("Become the top fighter in the heroes' battle rankings", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void FlagsLeakedCorrectedLabel(string? qcTranslated, bool expected)
    {
        Assert.Equal(expected, QualityReviewHelpers.IsCorrectedLabelLeak(qcTranslated));
    }

    // Regression test for the corrupted QcTranslated values found in DragonHierOverLlm's
    // AchievementData.csv.yaml - even if a leaked "CORRECTED:" label somehow made it into
    // QcTranslated (from any code path, not just QualityReviewWorkflow's own parsing),
    // IsQcReviewFresh must refuse to vouch for it so every packaging path falls back to the
    // pre-QC Translated text instead of shipping the corrupted string.
    [Fact(DisplayName = "IsQcReviewFresh refuses to trust a QcTranslated with a leaked CORRECTED: label")]
    public void IsQcReviewFreshRejectsCorrectedLabelLeak()
    {
        var split = new TranslationSplit
        {
            Translated = "Rich beyond measure",
            QcStatus = QcStatus.Corrected,
            QcReviewedText = "Rich beyond measure",
            QcTranslated = "Wealth in the millions CORRECTED: NONE",
        };

        var fresh = QualityReviewHelpers.IsQcReviewFresh(split, null, [split], EnabledConfig);

        Assert.False(fresh);
    }

    [Fact(DisplayName = "IsQcReviewFresh trusts a clean, matching QcTranslated")]
    public void IsQcReviewFreshAcceptsCleanCorrection()
    {
        var split = new TranslationSplit
        {
            Translated = "Rich beyond measure",
            QcStatus = QcStatus.Corrected,
            QcReviewedText = "Rich beyond measure",
            QcTranslated = "Wealth in the millions",
        };

        var fresh = QualityReviewHelpers.IsQcReviewFresh(split, null, [split], EnabledConfig);

        Assert.True(fresh);

        var disabledFresh = QualityReviewHelpers.IsQcReviewFresh(split, null, [split], new QualityReviewConfig { Enabled = false });
        Assert.False(disabledFresh);
    }

    [Fact(DisplayName = "ResetQcState clears stored QC outcome but preserves the retry counter")]
    public void ResetQcStateClearsReviewState()
    {
        var split = new TranslationSplit
        {
            QcTranslated = "A corrected translation",
            QcStatus = QcStatus.Corrected,
            QcReviewedText = "An older translation",
            FlaggedForQcReview = true,
            QcRejectedCorrection = "A rejected correction",
            QcFailureReason = "failed validation",
            QcQualityScore = 42,
            QcDefectCategory = QcDefectCategory.DomainTerm,
            QcRuleCheckFailureCount = 2,
            QcRuleCheckFailureBaseline = "An older translation",
        };

        split.ResetQcState();

        Assert.Equal(string.Empty, split.QcTranslated);
        Assert.Equal(QcStatus.NotReviewed, split.QcStatus);
        Assert.Equal(string.Empty, split.QcReviewedText);
        Assert.False(split.FlaggedForQcReview);
        Assert.Equal(string.Empty, split.QcRejectedCorrection);
        Assert.Equal(string.Empty, split.QcFailureReason);
        Assert.Null(split.QcQualityScore);
        Assert.Equal(QcDefectCategory.Unknown, split.QcDefectCategory);
        Assert.Equal(2, split.QcRuleCheckFailureCount);
        Assert.Equal("An older translation", split.QcRuleCheckFailureBaseline);
    }
}
