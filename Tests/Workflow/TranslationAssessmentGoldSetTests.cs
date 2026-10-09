using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace FanslationStudio.LlmKit.Tests.Workflow;

public class TranslationAssessmentGoldSetTests
{
    [Fact(DisplayName = "Translation assessment (gold set) - one sample per case, with each case's glossary snapshot")]
    public void LoadsOneSamplePerCaseWithSnapshots()
    {
        var goldSet = new QualityControlAssessmentWorkflow.GoldSet
        {
            SchemaVersion = 2,
            Items =
            [
                new() { SampleId = "a", Source = "掌门来了", Candidates = { ["m"] = "t" }, Glossary = [new("掌门", "Sect Leader")] },
                new() { SampleId = "b", Source = "没有术语", Candidates = { ["m"] = "t" } },
            ],
            CorrectionSamples = [new() { SampleId = "c", Source = "再见", CurrentTranslation = "bye", ProposedCorrection = "goodbye", Glossary = [] }],
        };
        var dir = Directory.CreateTempSubdirectory("llmkit-gold-translation-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "gold.yaml"), YamlHelper.CreateSerializer().Serialize(goldSet));

            var (samples, glossaries) = TranslationAssessmentWorkflow.LoadGoldSetSamples(dir, "gold.yaml");

            Assert.Equal(["a", "b", "c"], samples.Select(s => s.SampleId));
            Assert.All(samples, s => Assert.Equal(string.Empty, s.FilePath));
            Assert.Equal(["a", "c"], glossaries.Keys.Order());
            Assert.Equal("Sect Leader", glossaries["a"][0].Result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory(DisplayName = "Translation assessment (gold set) - deterministic findings on an output")]
    [InlineData("他说", "He said", "")]
    [InlineData("他说", "He said 你好", "LeftoverCjk")]
    public void DetectsKnownDefectShapes(string source, string translation, string expected)
    {
        var findings = TranslationAssessmentWorkflow.DetectFindings(source, translation);
        Assert.Equal(expected == "" ? [] : new[] { expected }, findings);
    }
}
