using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Tests;

/// <summary>
/// Covers <see cref="GameFileHandlingBase.MergeFilesIntoTranslatedAsync"/> - specifically the
/// JSON (SplitPath-based) match branch. Regression test for a bug where a stale whole-cell
/// translation (e.g. "功力+100" -&gt; "Power +100", from before the cell was decomposed into a
/// template) got silently carried over onto a freshly re-decomposed split whose Text had shrunk
/// to just the stem ("功力"), because the match only checked SplitPath+SubIndex and never
/// compared Text. Packaging then reconstructed "{0}+100".Replace("{0}", "Power +100"), producing
/// a doubled "Power +100+100" in game. See WanXiangOverLlm/Files/Converted/Talent.json.yaml
/// rawIndex 10020 for the real-world instance.
/// </summary>
public class GameFileHandlingBaseTests
{
    private static string CreateWorkingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameFileHandlingBaseTests_" + Guid.NewGuid());
        Directory.CreateDirectory($"{dir}/Converted");
        Directory.CreateDirectory($"{dir}/Raw/Export");
        return dir;
    }

    private static TextFileToSplit TestFile(string path) => new()
    {
        Path = path,
        TextFileType = TextFileType.RawJson,
        PackageOutput = true,
    };

    [Fact(DisplayName = "Merge does not inherit a stale whole-cell translation onto a split whose Text shrank after the cell was decomposed into a template")]
    public async Task MergeFilesIntoTranslatedAsync_DoesNotCarryOverStaleTranslationWhenTextChanged()
    {
        var dir = CreateWorkingDirectory();
        try
        {
            var oldConverted = new List<TranslationLine>
            {
                new()
                {
                    RawIndex = "10020",
                    Splits =
                    [
                        new TranslationSplit(0, "功力+100") { SplitPath = "Desc", Translated = "Power +100" },
                    ],
                },
            };
            File.WriteAllText($"{dir}/Converted/Talent.json.yaml", YamlHelper.CreateSerializer().Serialize(oldConverted));

            var freshExport = new List<TranslationLine>
            {
                new()
                {
                    RawIndex = "10020",
                    Splits =
                    [
                        new TranslationSplit(0, "功力") { SplitPath = "Desc" },
                    ],
                    Templates = [new FieldTemplate { SplitPath = "Desc", Template = "{0}+100" }],
                },
            };
            File.WriteAllText($"{dir}/Raw/Export/Talent.json.yaml", YamlHelper.CreateSerializer().Serialize(freshExport));

            await GameFileHandlingBase.MergeFilesIntoTranslatedAsync(dir, [TestFile("Talent.json")]);

            var merged = YamlHelper.CreateDeserializer()
                .Deserialize<List<TranslationLine>>(File.ReadAllText($"{dir}/Converted/Talent.json.yaml"));

            var descSplit = merged.Single().Splits.Single(s => s.SplitPath == "Desc");
            Assert.Equal("功力", descSplit.Text);
            Assert.NotEqual("Power +100", descSplit.Translated);
            Assert.True(string.IsNullOrEmpty(descSplit.Translated));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
