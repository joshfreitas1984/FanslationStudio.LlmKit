using FanslationStudio.LlmKit.Workflow;

namespace FanslationStudio.LlmKit.Assessments;

/// <summary>Deterministic checks on the checked-in gold set; no LLM, safe for CI.</summary>
public class GoldSetTests
{
    [Fact(DisplayName = "Gold set - every case is self-contained (game and glossary snapshot)")]
    public void EveryCaseIsSelfContained()
    {
        if (!File.Exists(AssessmentPaths.GoldSet))
            return; // not imported yet

        var goldSet = QualityControlAssessmentWorkflow.LoadGoldSet(File.ReadAllText(AssessmentPaths.GoldSet));
        Assert.Equal(2, goldSet.SchemaVersion);

        var missing = goldSet.Items.Where(x => x.Glossary == null || x.Game == "").Select(x => x.SampleId)
            .Concat(goldSet.CorrectionSamples.Where(x => x.Glossary == null || x.Game == "").Select(x => x.SampleId))
            .ToList();
        Assert.True(missing.Count == 0, "Cases without a game and glossary snapshot: " + string.Join(", ", missing.Take(20)));
    }
}
