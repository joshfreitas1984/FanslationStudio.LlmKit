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
}