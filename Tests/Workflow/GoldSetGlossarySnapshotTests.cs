using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace FanslationStudio.LlmKit.Tests.Workflow;

public class GoldSetGlossarySnapshotTests
{
    private static List<GlossaryLine> Glossary() =>
    [
        new("三七", "Sanqi"),
        new("三七开", "70/30 split"),
        new("掌门", "Sect Leader"),
        new("无关", "Unrelated"),
    ];

    private static QualityControlAssessmentWorkflow.GoldSet GoldSet() => new()
    {
        SchemaVersion = 1,
        Items = [new() { SampleId = "a", Source = "三七开，掌门说", Candidates = { ["x"] = "t" } }],
        CorrectionSamples = [new() { SampleId = "b", Source = "掌门", CurrentTranslation = "t", ProposedCorrection = "u" }],
    };

    [Fact(DisplayName = "Gold-set snapshot - keeps applicable, unshadowed entries and bumps the schema")]
    public void SnapshotsApplicableEntries()
    {
        var goldSet = GoldSet();
        Assert.Equal(2, GoldSetGlossarySnapshot.Apply(goldSet, Glossary(), "TestGame"));

        Assert.Equal(2, goldSet.SchemaVersion);
        Assert.Equal(["三七开", "掌门"], goldSet.Items[0].Glossary!.Select(g => g.Raw));
        Assert.Equal("TestGame", goldSet.Items[0].Game);
        Assert.Equal(["掌门"], goldSet.CorrectionSamples[0].Glossary!.Select(g => g.Raw));
    }

    [Fact(DisplayName = "Gold-set snapshot - existing snapshots are kept unless overwritten")]
    public void ExistingSnapshotsKept()
    {
        var goldSet = GoldSet();
        GoldSetGlossarySnapshot.Apply(goldSet, Glossary(), "A");
        Assert.Equal(0, GoldSetGlossarySnapshot.Apply(goldSet, [], "B"));
        Assert.Equal(2, GoldSetGlossarySnapshot.Apply(goldSet, [], "B", overwrite: true));
        Assert.Empty(goldSet.Items[0].Glossary!);
    }

    [Fact(DisplayName = "Gold-set snapshot - round-trips through YAML and keeps schema-1 files loadable")]
    public void RoundTrips()
    {
        var goldSet = GoldSet();
        GoldSetGlossarySnapshot.Apply(goldSet, Glossary(), "TestGame");
        var reloaded = QualityControlAssessmentWorkflow.LoadGoldSet(GoldSetGlossarySnapshot.Serialize(goldSet));
        Assert.Equal("掌门", reloaded.Items[0].Glossary![1].Raw);
        Assert.Equal("Sect Leader", reloaded.Items[0].Glossary![1].Result);

        var v1 = QualityControlAssessmentWorkflow.LoadGoldSet(YamlHelper.CreateSerializer().Serialize(GoldSet()));
        Assert.Null(v1.Items[0].Glossary);
    }

    [Fact(DisplayName = "Gold-set snapshot - fingerprint changes with the glossary but not for schema 1")]
    public void FingerprintCoversGlossary()
    {
        var plain = QualityControlAssessmentWorkflow.CalculateFingerprint(GoldSet());
        var snapshotted = GoldSet();
        GoldSetGlossarySnapshot.Apply(snapshotted, Glossary(), "G");
        Assert.NotEqual(plain, QualityControlAssessmentWorkflow.CalculateFingerprint(snapshotted));
        Assert.Equal(plain, QualityControlAssessmentWorkflow.CalculateFingerprint(GoldSet()));
    }

    [Fact(DisplayName = "Gold-set snapshot - YAML upgrade keeps unmodelled keys and snapshots each case")]
    public void YamlUpgradeIsLossless()
    {
        const string yaml = "schemaVersion: 1\nannotationStatus: human-reviewed\nitems:\n- sampleId: a\n  source: 三七开，掌门说\n  reviewNote: keep me.\n  candidates:\n    x: t\n  labels:\n    x:\n      label: Pass\ncorrectionSamples: []\n";
        var (upgraded, stamped) = GoldSetGlossarySnapshot.ApplyToYaml(yaml, Glossary(), "TestGame");

        Assert.Equal(1, stamped);
        Assert.Contains("annotationStatus: human-reviewed", upgraded);
        Assert.Contains("reviewNote: keep me.", upgraded);
        var goldSet = QualityControlAssessmentWorkflow.LoadGoldSet(upgraded);
        Assert.Equal(2, goldSet.SchemaVersion);
        Assert.Equal(["三七开", "掌门"], goldSet.Items[0].Glossary!.Select(g => g.Raw));
        Assert.Equal("TestGame", goldSet.Items[0].Game);

        var (again, restamped) = GoldSetGlossarySnapshot.ApplyToYaml(upgraded, Glossary(), "Other");
        Assert.Equal(0, restamped);
        Assert.Equal("TestGame", QualityControlAssessmentWorkflow.LoadGoldSet(again).Items[0].Game);
    }
}
