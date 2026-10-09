using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using YamlDotNet.RepresentationModel;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Upgrades a QC gold set to schema v2: stamps each case with its game and the glossary entries that
/// applied to its source, so the case gives the same verdict with no game present.
/// </summary>
public static class GoldSetGlossarySnapshot
{
    /// <summary>
    /// Snapshots <paramref name="glossary"/> onto every case without one (or every case when
    /// <paramref name="overwrite"/>), using the production selection (<see cref="GlossaryLine.SelectFor"/>:
    /// file scoping, longer-match shadowing). Returns the number of cases stamped.
    /// </summary>
    public static int Apply(QualityControlAssessmentWorkflow.GoldSet goldSet, List<GlossaryLine> glossary, string game, bool overwrite = false)
    {
        var stamped = 0;
        foreach (var item in goldSet.Items)
            stamped += Stamp(item.Source, item.SourceFile, glossary, game, overwrite, item.Glossary, (g, name, file) => { item.Glossary = g; item.Game = name; });
        foreach (var sample in goldSet.CorrectionSamples)
            stamped += Stamp(sample.Source, sample.SourceFile, glossary, game, overwrite, sample.Glossary, (g, name, file) => { sample.Glossary = g; sample.Game = name; });

        if (stamped > 0)
            goldSet.SchemaVersion = 2;
        return stamped;
    }

    private static int Stamp(string source, string sourceFile, List<GlossaryLine> glossary, string game, bool overwrite,
        List<GlossaryLine>? existing, Action<List<GlossaryLine>, string, string> set)
    {
        if (existing != null && !overwrite)
            return 0;
        set(GlossaryLine.SelectFor(source, glossary, sourceFile).Select(x => x.ToSnapshot()).ToList(), game, sourceFile);
        return 1;
    }

    /// <summary>
    /// The same upgrade applied to the YAML text directly, so every other key a case carries
    /// (<c>reviewNote</c>, <c>provenance</c>, <c>sampleRun</c>, top-level source metadata) survives -
    /// the typed model only knows the fields the evaluator reads. Returns the new YAML and the number of
    /// cases stamped.
    /// </summary>
    public static (string Yaml, int Stamped) ApplyToYaml(string yaml, List<GlossaryLine> glossary, string game, bool overwrite = false)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var stamped = 0;

        foreach (var listKey in new[] { "items", "correctionSamples" })
        {
            if (!root.Children.TryGetValue(new YamlScalarNode(listKey), out var list) || list is not YamlSequenceNode cases)
                continue;
            foreach (var node in cases.Children.OfType<YamlMappingNode>())
            {
                if (node.Children.ContainsKey(new YamlScalarNode("glossary")) && !overwrite)
                    continue;
                var source = (node.Children.TryGetValue(new YamlScalarNode("source"), out var s) ? (s as YamlScalarNode)?.Value : null) ?? string.Empty;
                var sourceFile = (node.Children.TryGetValue(new YamlScalarNode("sourceFile"), out var f) ? (f as YamlScalarNode)?.Value : null) ?? string.Empty;
                var snapshot = GlossaryLine.SelectFor(source, glossary, sourceFile).Select(x => x.ToSnapshot()).ToList();

                var glossaryStream = new YamlStream();
                glossaryStream.Load(new StringReader(YamlHelper.CreateSerializer().Serialize(snapshot)));
                node.Children[new YamlScalarNode("game")] = new YamlScalarNode(game);
                node.Children[new YamlScalarNode("glossary")] = glossaryStream.Documents[0].RootNode;
                stamped++;
            }
        }

        if (stamped > 0)
            root.Children[new YamlScalarNode("schemaVersion")] = new YamlScalarNode("2");

        var output = new StringWriter();
        stream.Save(output, assignAnchors: false);
        // Save ends the document with a "..." marker line; drop exactly that, never content.
        var text = output.ToString().TrimEnd((char)13, (char)10);
        if (text.EndsWith("..."))
            text = text[..^3].TrimEnd((char)13, (char)10);
        return (text + Environment.NewLine, stamped);
    }

    public static string Serialize(QualityControlAssessmentWorkflow.GoldSet goldSet) => YamlHelper.CreateSerializer().Serialize(goldSet);
}
