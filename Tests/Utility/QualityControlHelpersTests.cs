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

    // Regression test: retranslating a non-zero SubIndex fragment of a compound/templated column
    // used to call TranslationSplit.ResetQcState() on the retranslated fragment itself, which never
    // carries QC state for a compound column - only the SubIndex == 0 fragment does (see the anchor
    // convention in docs/features/translation-pipeline/quality-review-pass.md). That left the anchor's
    // stale QcStatus/QcQualityScore/QcTranslated sitting untouched until IsQcReviewFresh's own dynamic
    // recompute caught up on the next QC run - this test locks in that FindQcAnchor resolves the
    // correct fragment (the anchor) so every caller (TranslationWorkflow.UpdateSplit,
    // TranslationService's retranslation paths) resets the right one immediately instead.
    [Fact(DisplayName = "FindQcAnchor resolves the SubIndex == 0 fragment for a CSV-style compound column")]
    public void FindQcAnchorResolvesAnchorForCsvStyleColumn()
    {
        var anchorSplit = new TranslationSplit { Split = 1, SubIndex = 0, Text = "part0", Translated = "Part 0" };
        var subIndex1 = new TranslationSplit { Split = 1, SubIndex = 1, Text = "part1", Translated = "Part 1" };
        var subIndex2 = new TranslationSplit { Split = 1, SubIndex = 2, Text = "part2", Translated = "Part 2" };
        var otherColumn = new TranslationSplit { Split = 2, SubIndex = 0, Text = "unrelated", Translated = "Unrelated" };

        var line = new TranslationLine
        {
            Splits = [anchorSplit, subIndex1, subIndex2, otherColumn],
        };

        // Retranslating subIndex2 (not the anchor) must still resolve back to subIndex 0's fragment,
        // not to subIndex2 itself and not to the unrelated column sharing the same line.
        var resolved = QualityReviewHelpers.FindQcAnchor(line, subIndex2);

        Assert.Same(anchorSplit, resolved);
    }

    [Fact(DisplayName = "FindQcAnchor resolves the SubIndex == 0 fragment for a JSON-style field-path column")]
    public void FindQcAnchorResolvesAnchorForJsonStyleColumn()
    {
        // JSON field-path files leave Split == 0 for every field on the line (see
        // QualityReviewWorkflow.ColumnKey's own doc comment) and disambiguate columns via SplitPath
        // instead - this must be respected here too, or a JSON compound field's retranslated
        // non-zero-SubIndex fragment would get grouped with an unrelated field that also has
        // Split == 0.
        var anchorSplit = new TranslationSplit { Split = 0, SplitPath = "Desc", SubIndex = 0, Text = "part0", Translated = "Part 0" };
        var subIndex1 = new TranslationSplit { Split = 0, SplitPath = "Desc", SubIndex = 1, Text = "part1", Translated = "Part 1" };
        var unrelatedField = new TranslationSplit { Split = 0, SplitPath = "Name", SubIndex = 0, Text = "name", Translated = "Name" };

        var line = new TranslationLine
        {
            Splits = [anchorSplit, subIndex1, unrelatedField],
        };

        var resolved = QualityReviewHelpers.FindQcAnchor(line, subIndex1);

        Assert.Same(anchorSplit, resolved);
    }

    [Fact(DisplayName = "FindQcAnchor returns the split itself for a plain, single-fragment column")]
    public void FindQcAnchorReturnsSelfForPlainColumn()
    {
        var plainSplit = new TranslationSplit { Split = 3, SubIndex = 0, Text = "plain", Translated = "Plain" };
        var line = new TranslationLine { Splits = [plainSplit] };

        var resolved = QualityReviewHelpers.FindQcAnchor(line, plainSplit);

        Assert.Same(plainSplit, resolved);
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
