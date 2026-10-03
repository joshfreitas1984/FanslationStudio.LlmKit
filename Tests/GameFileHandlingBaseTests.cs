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

    [Theory(DisplayName = "Merge carries every Qc* field forward onto an unchanged split")]
    [InlineData(false)] // line matched by Raw
    [InlineData(true)]  // Raw changed - falls back to split-level matching
    public async Task MergeFilesIntoTranslatedAsync_CarriesAllQcStateForward(bool rawChanged)
    {
        var dir = CreateWorkingDirectory();
        try
        {
            var reviewed = new TranslationSplit(1, "大侠")
            {
                Translated = "Hero",
                QcTranslated = "Great Hero",
                QcStatus = QcStatus.Corrected,
                QcReviewedText = "Hero",
                FlaggedForQcReview = true,
                QcRejectedCorrection = "Big Shrimp",
                QcFailureReason = "reason",
                QcQualityScore = 42,
                QcDefectCategory = QcDefectCategory.UntranslatedPinyin,
                QcDefectCategories = [QcDefectCategory.UntranslatedPinyin, QcDefectCategory.OtherNamedDefect],
                QcRuleCheckFailureCount = 2,
                QcRuleCheckFailureBaseline = "Great Hero",
            };
            var oldConverted = new List<TranslationLine> { new() { Raw = "1,大侠", Splits = [reviewed] } };
            File.WriteAllText($"{dir}/Converted/Test.csv.yaml", YamlHelper.CreateSerializer().Serialize(oldConverted));

            var freshExport = new List<TranslationLine>
            {
                new() { Raw = rawChanged ? "2,大侠" : "1,大侠", Splits = [new TranslationSplit(1, "大侠")] },
            };
            File.WriteAllText($"{dir}/Raw/Export/Test.csv.yaml", YamlHelper.CreateSerializer().Serialize(freshExport));

            await GameFileHandlingBase.MergeFilesIntoTranslatedAsync(dir,
                [new TextFileToSplit { Path = "Test.csv", PackageOutput = true }]);

            var merged = YamlHelper.CreateDeserializer()
                .Deserialize<List<TranslationLine>>(File.ReadAllText($"{dir}/Converted/Test.csv.yaml"))
                .Single().Splits.Single();

            Assert.Equal("Hero", merged.Translated);
            Assert.Equal([QcDefectCategory.UntranslatedPinyin, QcDefectCategory.OtherNamedDefect], merged.QcDefectCategories);

            // Guard against the next Qc* field being added without updating CopyQcState.
            var qcProperties = typeof(TranslationSplit).GetProperties()
                .Where(p => p.Name.StartsWith("Qc") || p.Name == nameof(TranslationSplit.FlaggedForQcReview))
                .Where(p => p.PropertyType != typeof(List<QcDefectCategory>));
            foreach (var property in qcProperties)
                Assert.True(Equals(property.GetValue(reviewed), property.GetValue(merged)),
                    $"{property.Name} was not carried forward by the merge");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static async Task<List<TranslationLine>> MergeAsync(string path, List<TranslationLine> converted, List<TranslationLine> export)
    {
        var dir = CreateWorkingDirectory();
        try
        {
            File.WriteAllText($"{dir}/Converted/{path}.yaml", YamlHelper.CreateSerializer().Serialize(converted));
            File.WriteAllText($"{dir}/Raw/Export/{path}.yaml", YamlHelper.CreateSerializer().Serialize(export));

            await GameFileHandlingBase.MergeFilesIntoTranslatedAsync(dir, [new TextFileToSplit { Path = path, PackageOutput = true }]);

            return YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(File.ReadAllText($"{dir}/Converted/{path}.yaml"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static TranslationSplit Done(int split, int subIndex, string text, string translated, string splitPath = "") =>
        new(split, subIndex, text) { Translated = translated, SplitPath = splitPath };

    [Fact(DisplayName = "Merge matches a duplicated Raw or RawIndex against its first converted occurrence")]
    public async Task MergeFilesIntoTranslatedAsync_DuplicateLineKeysUseFirstOccurrence()
    {
        var converted = new List<TranslationLine>
        {
            new() { Raw = "1,大侠", Splits = [Done(1, 0, "大侠", "First")] },
            new() { Raw = "1,大侠", Splits = [Done(1, 0, "大侠", "Second")] },
            new() { RawIndex = "7", Raw = "{}", Splits = [Done(0, 0, "名字", "First Name", "Name")] },
            new() { RawIndex = "7", Raw = "{ }", Splits = [Done(0, 0, "名字", "Second Name", "Name")] },
        };
        var export = new List<TranslationLine>
        {
            new() { Raw = "1,大侠", Splits = [new TranslationSplit(1, "大侠")] },
            new() { RawIndex = "7", Raw = "{changed}", Splits = [new TranslationSplit(0, "名字") { SplitPath = "Name" }] },
        };

        var merged = await MergeAsync("Dupes.csv", converted, export);

        Assert.Equal("First", merged[0].Splits[0].Translated);
        Assert.Equal("First Name", merged[1].Splits[0].Translated);
    }

    [Fact(DisplayName = "Merge split-level fallback prefers the most specific key, then the earliest converted split")]
    public async Task MergeFilesIntoTranslatedAsync_FallbackPrefersSpecificKeyThenFirstOccurrence()
    {
        var converted = new List<TranslationLine>
        {
            new() { Raw = "old-a", Splits = [Done(5, 0, "甲", "Loose A"), Done(9, 0, "乙", "Loose B1")] },
            new() { Raw = "old-b", Splits = [Done(9, 0, "乙", "Loose B2"), Done(1, 0, "甲", "Exact A1"), Done(1, 0, "甲", "Exact A2")] },
            new() { Raw = "old-c", Splits = [Done(0, 1, "丙", "Path loose", "Desc"), Done(0, 3, "丙", "Path exact 1", "Desc")] },
            new() { Raw = "old-d", Splits = [Done(0, 3, "丙", "Path exact 2", "Desc"), Done(0, 3, "丙", "Wrong path", "Name")] },
        };
        var export = new List<TranslationLine>
        {
            new()
            {
                Raw = "new",
                Splits =
                [
                    new TranslationSplit(1, 0, "甲"),
                    new TranslationSplit(2, 0, "乙"),
                    new TranslationSplit(0, 3, "丙") { SplitPath = "Desc" },
                    new TranslationSplit(0, 7, "丙") { SplitPath = "Desc" },
                    new TranslationSplit(0, 0, "丙") { SplitPath = "Other" },
                    new TranslationSplit(3, 0, "丁"),
                ],
            },
        };

        var merged = (await MergeAsync("Fallback.csv", converted, export)).Single().Splits;

        Assert.Equal("Exact A1", merged[0].Translated);    // (Split, SubIndex, Text) beats an earlier Text-only match
        Assert.Equal("Loose B1", merged[1].Translated);    // Text-only: first in converted order
        Assert.Equal("Path exact 1", merged[2].Translated); // (SplitPath, SubIndex, Text) beats an earlier (SplitPath, Text)
        Assert.Equal("Path loose", merged[3].Translated);   // (SplitPath, Text): first in converted order
        Assert.Equal("", merged[4].Translated);             // a SplitPath split never falls back to Text-only
        Assert.Equal("", merged[5].Translated);
    }

    [Fact(DisplayName = "Merge within a matched line prefers the most specific split key, then the earliest split")]
    public async Task MergeFilesIntoTranslatedAsync_MatchedLinePrefersSpecificSplitKey()
    {
        var converted = new List<TranslationLine>
        {
            new() { Raw = "row", Splits = [Done(4, 0, "甲", "Loose"), Done(1, 0, "甲", "Exact"), Done(1, 0, "甲", "Exact later")] },
        };
        var export = new List<TranslationLine>
        {
            new() { Raw = "row", Splits = [new TranslationSplit(1, 0, "甲"), new TranslationSplit(2, 0, "甲")] },
        };

        var merged = (await MergeAsync("Matched.csv", converted, export)).Single().Splits;

        Assert.Equal("Exact", merged[0].Translated);
        Assert.Equal("Loose", merged[1].Translated);
    }
}
