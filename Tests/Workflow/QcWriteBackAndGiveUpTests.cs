using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

/// <summary>
/// QC write-back only touches files that actually changed (reset sweeps and the review pass), and
/// the rule check's give-up lands on the same terminal shape as the review pass's give-up.
/// </summary>
public sealed class QcWriteBackAndGiveUpTests : IDisposable
{
    private const string Marker = "# untouched-marker\n";
    private readonly string _workingDirectory = Directory.CreateTempSubdirectory("qc-writeback-").FullName;
    private readonly TextFileToSplit _textFile = new() { Path = "Test.csv" };

    public void Dispose()
    {
        try { Directory.Delete(_workingDirectory, true); } catch (IOException) { }
    }

    private string OutputFile => $"{_workingDirectory}/Converted/{_textFile.Path}.yaml";

    private void WriteCorpus(params TranslationSplit[] splits)
    {
        Directory.CreateDirectory($"{_workingDirectory}/Converted");
        var lines = splits.Select(split => new TranslationLine { Raw = split.Text, Splits = [split] }).ToList();
        // The marker comment is dropped by any re-serialization, so its presence proves no write happened.
        File.WriteAllText(OutputFile, Marker + YamlHelper.CreateSerializer().Serialize(lines));
    }

    private List<TranslationLine> ReadCorpus() =>
        YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(File.ReadAllText(OutputFile));

    [Fact(DisplayName = "Reset sweep that changes nothing leaves the file unwritten")]
    public async Task ResetWithNoMatchesDoesNotWrite()
    {
        WriteCorpus(new TranslationSplit { Split = 0, Text = "甲", Translated = "A", QcStatus = QcStatus.Passed, QcReviewedText = "A" });

        await QualityReviewWorkflow.ResetCorrectedQcState(_workingDirectory, [_textFile]);
        await QualityReviewWorkflow.ResetQcRetryLimits(_workingDirectory, [_textFile]);

        Assert.StartsWith(Marker, File.ReadAllText(OutputFile));
    }

    [Fact(DisplayName = "Reset sweep that changes a column writes the file")]
    public async Task ResetWithMatchWrites()
    {
        WriteCorpus(new TranslationSplit { Split = 0, Text = "甲", Translated = "A", QcStatus = QcStatus.Corrected, QcTranslated = "AA", QcReviewedText = "A" });

        await QualityReviewWorkflow.ResetCorrectedQcState(_workingDirectory, [_textFile]);

        Assert.DoesNotContain("untouched-marker", File.ReadAllText(OutputFile));
        Assert.Equal(QcStatus.NotReviewed, ReadCorpus()[0].Splits[0].QcStatus);
    }

    [Fact(DisplayName = "ResetAllQcState skips an already-clean corpus but clears stray state")]
    public async Task ResetAllOnlyWritesWhenStateExists()
    {
        WriteCorpus(new TranslationSplit { Split = 0, Text = "甲", Translated = "A" });
        await QualityReviewWorkflow.ResetAllQcState(_workingDirectory, [_textFile]);
        Assert.StartsWith(Marker, File.ReadAllText(OutputFile));

        WriteCorpus(new TranslationSplit { Split = 0, Text = "甲", Translated = "A", QcRuleCheckFailureCount = 2 });
        await QualityReviewWorkflow.ResetAllQcState(_workingDirectory, [_textFile]);
        Assert.Equal(0, ReadCorpus()[0].Splits[0].QcRuleCheckFailureCount);
    }

    [Fact(DisplayName = "Review pass with nothing to review never rewrites the file")]
    public async Task ReviewPassWithNoWorkDoesNotWrite()
    {
        // Not ready for review (still flagged for retranslation) - never dispatched.
        WriteCorpus(new TranslationSplit { Split = 0, Text = "甲", Translated = "A", FlaggedForRetranslation = true });
        var config = new LlmConfig { QualityReview = new QualityReviewConfig { Enabled = true } };

        var fileStates = await QualityReviewWorkflow.LoadFileStatesAsync(_workingDirectory, [_textFile]);
        var reviewed = await QualityReviewWorkflow.ReviewFileStatesAsync(config, new ModelExecutionConfig(), fileStates, null);

        Assert.Equal(0, reviewed);
        Assert.StartsWith(Marker, File.ReadAllText(OutputFile));
    }

    [Fact(DisplayName = "Rule-check give-up sets QcDefectCategories the same way the review pass's give-up does")]
    public void RuleCheckGiveUpSetsDefectCategories()
    {
        var config = new LlmConfig { Hooks = new GameHooks(), QualityReview = new QualityReviewConfig { Enabled = true, MaxRuleCheckRetries = 0 } };
        var model = new ModelExecutionConfig();
        config.Runtime.Models["Default"] = model;
        var anchor = new TranslationSplit
        {
            Split = 0,
            Text = "全盔",
            Translated = "Full helmet",
            QcStatus = QcStatus.Corrected,
            QcTranslated = "FULL HELMET",
            QcReviewedText = "Full helmet",
            QcQualityScore = 90,
            QcDefectCategory = QcDefectCategory.DomainTerm,
            QcDefectCategories = [QcDefectCategory.DomainTerm],
        };

        var (changed, needsRetry, gaveUp) = QualityReviewWorkflow.ApplyRulesToQcColumn(config, model, anchor, _textFile, "全盔", "Full helmet");

        Assert.True(changed);
        Assert.False(needsRetry);
        Assert.True(gaveUp);
        Assert.Equal(QcStatus.FailedValidation, anchor.QcStatus);
        Assert.Equal(QcDefectCategory.Unknown, anchor.QcDefectCategory);
        Assert.Equal([QcDefectCategory.Unknown], anchor.QcDefectCategories);
        Assert.Null(anchor.QcQualityScore);
        Assert.True(anchor.FlaggedForQcReview);
        Assert.Empty(anchor.QcTranslated);
        Assert.Equal("Full helmet", anchor.QcReviewedText);
        Assert.Equal("FULL HELMET", anchor.QcRejectedCorrection);
    }
}
