using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public sealed class QualityEvaluatorAssessmentWorkflowTests
{
    [Fact]
    public void LoadGoldSet_FlattensCurrentCandidateShape()
    {
        var goldSet = QualityEvaluatorAssessmentWorkflow.LoadGoldSet("""
            schemaVersion: 1
            labelVersion: test
            items:
            - sampleId: sample-1
              source: 原文
              candidates:
                Generator-A: 译文 A
                Generator-B: 译文 B
              labels:
                Generator-A:
                  label: Pass
                  defectCategories: []
                Generator-B:
                  label: Defect
                  defectCategories: [omitted-separator]
            correctionSamples:
            - sampleId: correction-1
              source: 原文
              currentTranslation: 当前译文
              label: Defect
              defectCategories: [dropped-content]
              proposedCorrection: 修正译文
              correctionSafety: safe
            """);

        Assert.Single(goldSet.Items);
        Assert.Equal("译文 B", goldSet.Items[0].Candidates["Generator-B"]);
        Assert.Equal("Defect", goldSet.Items[0].Labels["Generator-B"].Label);
        Assert.Single(goldSet.CorrectionSamples);
        Assert.Equal("safe", goldSet.CorrectionSamples[0].CorrectionSafety);
    }

    [Fact]
    public void LoadGoldSet_RejectsDuplicateSampleIds()
    {
        var yaml = """
            schemaVersion: 1
            items:
            - sampleId: duplicate
              source: one
              candidates: { Model: translation }
              labels: { Model: { label: Pass } }
            correctionSamples:
            - sampleId: duplicate
              source: two
              currentTranslation: translation
              label: Pass
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            QualityEvaluatorAssessmentWorkflow.LoadGoldSet(yaml));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalculateFingerprint_ChangesWhenReviewedLabelChanges()
    {
        var first = QualityEvaluatorAssessmentWorkflow.LoadGoldSet("""
            schemaVersion: 1
            items:
            - sampleId: sample-1
              source: 原文
              candidates: { Model: 译文 }
              labels: { Model: { label: Pass } }
            """);
        var second = QualityEvaluatorAssessmentWorkflow.LoadGoldSet("""
            schemaVersion: 1
            items:
            - sampleId: sample-1
              source: 原文
              candidates: { Model: 译文 }
              labels: { Model: { label: Defect } }
            """);

        Assert.NotEqual(
            QualityEvaluatorAssessmentWorkflow.CalculateFingerprint(first),
            QualityEvaluatorAssessmentWorkflow.CalculateFingerprint(second));
    }

        [Fact]
        public void Summary_SeparatesUnscoredDetectionFromFalsePositives()
        {
          var report = new QualityEvaluatorAssessmentWorkflow.EvaluatorResultFile
          {
            ModelName = "Evaluator",
            Results =
            [
                new() { EvaluationKind = "detection", ExpectedLabel = "Pass", ActualLabel = "Defect", ParseSuccess = false },
              new() { EvaluationKind = "detection", ExpectedLabel = "Defect", ActualLabel = "Pass", ParseSuccess = true },
              new() { EvaluationKind = "detection", ExpectedLabel = "Defect", ActualLabel = "Defect", ActualDefectCategory = "DroppedContent", ExpectedDefectCategories = ["dropped-content"], ParseSuccess = true },
            ],
          };

          var summary = report.ToSummary();

          Assert.Equal(1, summary.DetectionUnscoredCount);
          Assert.Equal(0, summary.FalsePositiveCount);
          Assert.Equal(1, summary.FalseNegativeCount);
          Assert.Equal(0.5, summary.DetectionAccuracy);
          Assert.Equal(0.5, summary.DefectRecall);
          Assert.Equal(1, summary.DefectCategoryAccuracy);
        }

        [Fact]
        public void Summary_ReportsCorrectionSafetyOutcomes()
        {
          var report = new QualityEvaluatorAssessmentWorkflow.EvaluatorResultFile
          {
            ModelName = "Evaluator",
            Results =
            [
              new() { EvaluationKind = "correction", ExpectedCorrectionSafety = "safe", ActualCorrectionSafety = "Safe", ParseSuccess = true },
              new() { EvaluationKind = "correction", ExpectedCorrectionSafety = "harmful", ActualCorrectionSafety = "Safe", ParseSuccess = true },
              new() { EvaluationKind = "correction", ExpectedCorrectionSafety = "unnecessary", ActualCorrectionSafety = "Unnecessary", ParseSuccess = true },
            ],
          };

          var summary = report.ToSummary();

          Assert.Equal(2, summary.CorrectionSafeCount);
          Assert.Equal(1, summary.CorrectionUnnecessaryCount);
          Assert.Equal(0, summary.CorrectionHarmfulCount);
          Assert.Equal(2d / 3d, summary.CorrectionSafetyAccuracy);
        }
}