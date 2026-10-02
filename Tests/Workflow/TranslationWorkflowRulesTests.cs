using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public class TranslationWorkflowRulesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"twr-{Guid.NewGuid():N}");

    public TranslationWorkflowRulesTests() => Directory.CreateDirectory($"{_dir}/Converted");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private static LlmConfig ConfigWithGlossary(params GlossaryLine[] lines)
    {
        var config = new LlmConfig();
        config.Runtime.GlossaryLines = [.. lines];
        return config;
    }

    [Theory(DisplayName = "FindGlossaryHallucination treats glossary results as literal text")]
    [InlineData("Elder (Retired)", "The Elder (Retired) arrives.", true)]
    [InlineData("Elder (Retired)", "The Elder Retired arrives.", false)]
    [InlineData("Mr. Li", "Mrx Li waves.", false)]
    [InlineData("Mr. Li", "Mr. Li waves.", true)]
    [InlineData("Li", "Lin waves.", false)]
    [InlineData("Li", "Li waves.", true)]
    public void FindGlossaryHallucination_LiteralTerms(string result, string translated, bool expectHallucination)
    {
        var config = ConfigWithGlossary(new GlossaryLine("李", result) { CheckForMisusedTranslation = true });

        var reason = TranslationWorkflow.FindGlossaryHallucination("你好", translated, config, new TextFileToSplit { Path = "A.txt" });

        Assert.Equal(expectHallucination, reason != null);
    }

    private void WriteConverted(string path, params TranslationSplit[] splits) =>
        File.WriteAllText($"{_dir}/Converted/{path}.yaml", YamlHelper.CreateSerializer().Serialize(
            splits.Select(s => new TranslationLine { Raw = s.Text, Splits = [s] }).ToList()));

    private List<TranslationLine> ReadConverted(string path) =>
        YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(File.ReadAllText($"{_dir}/Converted/{path}.yaml"));

    [Fact]
    public async Task SetSplitAsInvalid_WritesOnlyFilesWithMatches()
    {
        WriteConverted("A.txt", new TranslationSplit { Text = "坏字", Translated = "bad" });
        WriteConverted("B.txt", new TranslationSplit { Text = "好", Translated = "good" });
        var stamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc($"{_dir}/Converted/B.txt.yaml", stamp);

        await TranslationWorkflow.SetSplitAsInvalid(_dir, [new() { Path = "A.txt" }, new() { Path = "B.txt" }], ["坏"]);

        var a = ReadConverted("A.txt")[0].Splits[0];
        Assert.True(a.FlaggedForRetranslation);
        Assert.Equal("Bad Character", a.FlaggedMistranslation);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc($"{_dir}/Converted/B.txt.yaml"));
    }

    [Fact]
    public async Task CleanUpSomeRegexes_AppliesEveryReplacement()
    {
        WriteConverted("A.txt", new TranslationSplit { Text = "x", Translated = "foo  bar" });

        await TranslationWorkflow.CleanUpSomeRegexes(_dir, [new() { Path = "A.txt" }], [(@"\s{2,}", " "), ("bar", "baz")]);

        Assert.Equal("foo baz", ReadConverted("A.txt")[0].Splits[0].Translated);
    }

    [Fact]
    public async Task ResetAllFlags_ClearsFlags()
    {
        WriteConverted("A.txt", new TranslationSplit { Text = "x", Translated = "y", FlaggedForRetranslation = true, FlaggedMistranslation = "m" });

        await TranslationWorkflow.ResetAllFlags(_dir, [new() { Path = "A.txt" }]);

        var split = ReadConverted("A.txt")[0].Splits[0];
        Assert.False(split.FlaggedForRetranslation);
        Assert.Equal(string.Empty, split.FlaggedMistranslation);
    }
}
