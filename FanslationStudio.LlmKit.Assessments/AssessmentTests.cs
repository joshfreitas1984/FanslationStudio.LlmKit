using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;

namespace FanslationStudio.LlmKit.Assessments;

public class AssessmentTests
{
    [ManualFact(DisplayName = "1. Assess configured QC models")]
    public async Task AssessConfiguredQcModels()
    {
        await QualityControlAssessmentWorkflow.RunAsync(AssessmentPaths.WorkingDirectory);
    }

    /// <summary>
    /// One-off upgrade of a game's schema-1 gold set into this host's schema-2 set: stamps each case
    /// with the game and the glossary entries that applied to it (preset + that game's glossary).
    /// Idempotent - cases that already carry a snapshot are kept.
    /// </summary>
    [ManualFact(DisplayName = "2. Import a game's gold set (snapshot glossaries)")]
    public void ImportGameGoldSet()
    {
        const string game = "DragonHierOverLlm";
        var gameFiles = Path.GetFullPath(Path.Combine(AssessmentPaths.WorkingDirectory, "..", "..", "..", game, "Files"));
        var source = Path.Combine(gameFiles, "Goldset", "GoldSet.yaml");
        Assert.True(File.Exists(source), $"Sibling game gold set not found: {source}");

        var yaml = File.Exists(AssessmentPaths.GoldSet) ? File.ReadAllText(AssessmentPaths.GoldSet) : File.ReadAllText(source);
        var config = ConfigurationExtensions.GetConfiguration(gameFiles);
        var (upgraded, stamped) = GoldSetGlossarySnapshot.ApplyToYaml(yaml, config.Runtime.GlossaryLines, game);

        File.WriteAllText(AssessmentPaths.GoldSet, upgraded);
        // The snapshot must reproduce the exact glossary prompt the game's live glossary gives each case.
        var written = QualityControlAssessmentWorkflow.LoadGoldSet(upgraded);
        var mismatches = written.Items.Select(x => (x.SampleId, x.Source, x.Glossary))
            .Concat(written.CorrectionSamples.Select(x => (x.SampleId, x.Source, x.Glossary)))
            .Where(x => GlossaryLine.AppendPromptsFor(x.Source, config.Runtime.GlossaryLines, string.Empty)
                        != GlossaryLine.AppendPromptsFor(x.Source, x.Glossary!, string.Empty))
            .Select(x => x.SampleId).ToList();
        Assert.True(mismatches.Count == 0, "Snapshot prompt differs from the live glossary prompt: " + string.Join(", ", mismatches.Take(20)));
        Console.WriteLine($"Snapshotted {stamped} cases into {AssessmentPaths.GoldSet}");
    }
}
