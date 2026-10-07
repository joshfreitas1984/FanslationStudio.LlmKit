using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;
using SharedAssembly.DynamicStrings;

namespace FanslationStudio.LlmKit.Tests.Utility;

/// <summary>
/// Covers the tag/color regex helpers, export helpers, glossary analysis, failed-translation
/// reporting and <see cref="DynamicStringSupport"/>.
/// </summary>
public class SharedHelperTests
{
    [Fact(DisplayName = "TrimHtmlTagsInContent tidies spaced and self-closing tags")]
    public void TrimHtmlTagsInContent_TidiesTags()
    {
        Assert.Equal("<b>x", HtmlTagHelpers.TrimHtmlTagsInContent("< b >x"));
        Assert.Equal("<img src=a/>", HtmlTagHelpers.TrimHtmlTagsInContent("<img src=a  />"));
    }

    [Fact(DisplayName = "ExtractTagsListWithAttributes returns opening tags minus ignored prefixes")]
    public void ExtractTagsListWithAttributes_SkipsIgnored()
    {
        Assert.Equal(["<b>"], HtmlTagHelpers.ExtractTagsListWithAttributes("<color=red>a</color><b>", "color"));
    }

    [Fact(DisplayName = "ValidateTags compares raw and translated tag sets")]
    public void ValidateTags_ComparesTagSets()
    {
        Assert.True(HtmlTagHelpers.ValidateTags("<b>x</b>", "<b>y</b>", false).IsValid);

        var result = HtmlTagHelpers.ValidateTags("<b>x</b>", "y", false);
        Assert.False(result.IsValid);
        Assert.Equal(new HashSet<string> { "b", "/b" }, result.MissingTags);
    }

    [Theory(DisplayName = "StartsWithHalfColorTag detects an unclosed leading color tag")]
    [InlineData("<color=#fff>Hello", true, "<color=#fff>", "Hello")]
    [InlineData("<color=#fff>Hi</color>", false, "", "")]
    [InlineData("Hello", false, "", "")]
    public void StartsWithHalfColorTag_DetectsUnclosedTag(string input, bool expected, string expectedStart, string expectedEnd)
    {
        Assert.Equal(expected, ColorTagHelpers.StartsWithHalfColorTag(input, out var start, out var end));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Fact(DisplayName = "WriteExport always rewrites Raw/Export but never overwrites Converted")]
    public void WriteExport_NeverOverwritesConverted()
    {
        var dir = Directory.CreateTempSubdirectory("llmkit-export-helpers-").FullName;
        try
        {
            var textFile = new TextFileToSplit { Path = "File.txt" };
            ExportHelpers.WriteExport(dir, textFile, [new TranslationLine { Raw = "first" }]);
            ExportHelpers.WriteExport(dir, textFile, [new TranslationLine { Raw = "second" }]);

            var deserializer = YamlHelper.CreateDeserializer();
            Assert.Equal("second", deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText($"{dir}/Raw/Export/File.txt.yaml")).Single().Raw);
            Assert.Equal("first", deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText($"{dir}/Converted/File.txt.yaml")).Single().Raw);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact(DisplayName = "DecomposeFlatLine keeps a line with no fragments as one whole-line split")]
    public void DecomposeFlatLine_NoFragmentsKeepsWholeLine()
    {
        var line = ExportHelpers.DecomposeFlatLine("abc", null, false);

        Assert.Equal("abc", line.Raw);
        Assert.Empty(line.Templates);
        Assert.Equal("abc", Assert.Single(line.Splits).Text);
    }

    [Fact(DisplayName = "Glossary analysis finds containment conflicts and near-duplicate raws")]
    public void AnalyseGlossaryForIssues_FindsConflictsAndSimilarEntries()
    {
        var conflicts = GlossaryWorkflow.AnalyseGlossaryForIssues(
        [
            new GlossaryLine { Raw = "天下第一", Result = "Number One" },
            new GlossaryLine { Raw = "天下", Result = "World" },
            new GlossaryLine { Raw = "天", Result = "Sky", AllowedAlternatives = ["Number One"] },
        ]).ConflictingEntries;

        // "天" inside "天下第一" is allowed via its "Number One" alternative; the other two are not.
        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, c => c.Contains("has '天下' in '天下第一'"));
        Assert.Contains(conflicts, c => c.Contains("has '天' in '天下'"));

        var similar = GlossaryWorkflow.AnalyseGlossaryForIssues(
        [
            new GlossaryLine { Raw = "abcdefghij", Result = "R1", CheckForBadTranslation = false },
            new GlossaryLine { Raw = "abcdefghik", Result = "R2", CheckForBadTranslation = false },
            new GlossaryLine { Raw = "abc", Result = "R3", CheckForBadTranslation = false },
            new GlossaryLine { Raw = "Hello", Result = "R4", CheckForBadTranslation = false },
            new GlossaryLine { Raw = "hello", Result = "R5", CheckForBadTranslation = false },
            new GlossaryLine { Raw = "abcdefghiz", Result = "R1", CheckForBadTranslation = false },
        ]).SimilarEntries;

        Assert.Equal(3, similar.Count);
        Assert.Contains(similar, s => s.Contains("raw1: \"abcdefghij\"") && s.Contains("raw2: \"abcdefghik\""));
        Assert.Contains(similar, s => s.Contains("raw1: \"abcdefghik\"") && s.Contains("raw2: \"abcdefghiz\""));
        Assert.Contains(similar, s => s.Contains("raw1: \"Hello\""));
    }

    [Fact(DisplayName = "Glossary analysis reports a multi-character badtrans-off entry contained in a longer entry")]
    public void AnalyseGlossaryForIssues_ReportsPromptOnlyContainment()
    {
        var conflicts = GlossaryWorkflow.AnalyseGlossaryForIssues(
        [
            new GlossaryLine { Raw = "三七", Result = "Sanqi", CheckForBadTranslation = false },
            new GlossaryLine { Raw = "三七开", Result = "70/30", CheckForBadTranslation = false },
        ]).ConflictingEntries;

        var conflict = Assert.Single(conflicts);
        Assert.Contains("has '三七' in '三七开'", conflict);
        Assert.Contains("badtrans = false", conflict);
    }

    [Fact(DisplayName = "Glossary analysis ignores a single-character badtrans-off entry contained in a longer entry")]
    public void AnalyseGlossaryForIssues_IgnoresSingleCharacterPromptOnlyContainment()
    {
        var conflicts = GlossaryWorkflow.AnalyseGlossaryForIssues(
        [
            new GlossaryLine { Raw = "刚", Result = "Hard", CheckForBadTranslation = false },
            new GlossaryLine { Raw = "熊刚", Result = "Xiong Gang", CheckForBadTranslation = false },
        ]).ConflictingEntries;

        Assert.Empty(conflicts);
    }

    [Fact(DisplayName = "GetFailedTranslations reports untranslated CJK splits and dedupes short glossary candidates in order")]
    public async Task GetFailedTranslations_ReportsAndDedupes()
    {
        var dir = Directory.CreateTempSubdirectory("llmkit-failed-translations-").FullName;
        try
        {
            Directory.CreateDirectory($"{dir}/Converted");
            var lines = new List<TranslationLine>
            {
                new() { Raw = "1", Splits = [new TranslationSplit(0, "你好"), new TranslationSplit(1, "abc")] },
                new() { Raw = "2", Splits = [new TranslationSplit(0, "世界") { Translated = "World", FlaggedForRetranslation = true }] },
                new() { Raw = "3", Splits = [new TranslationSplit(0, "你好") { FlaggedMistranslation = "bad" }] },
                new() { Raw = "4", Splits = [new TranslationSplit(0, "世界很大很大很大")] },
                new() { Raw = "5", Splits = [new TranslationSplit(0, "完成") { Translated = "Done" }] },
            };
            File.WriteAllText($"{dir}/Converted/File.txt.yaml", YamlHelper.CreateSerializer().Serialize(lines));

            var (failures, forTheGlossary) = await GameFileHandlingBase.GetFailedTranslations(dir, [new TextFileToSplit { Path = "File.txt" }]);

            Assert.Equal(["你好", "世界", "你好", "世界很大很大很大"], failures.Select(f => f.Text));
            Assert.Equal(["你好", "世界"], forTheGlossary);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory(DisplayName = "IsSafeContract applies the type, method and combination skip lists")]
    [InlineData("GmManager", "Foo", false, false)]
    [InlineData("Some.GmManagerView", "Foo", false, false)]
    [InlineData("Foo", "LoadCSV", false, false)]
    [InlineData("LootItem", "Init", false, false)]
    [InlineData("LootItem", "Init", true, true)]
    [InlineData("Foo", "Bar", false, true)]
    public void IsSafeContract_AppliesSkipLists(string type, string method, bool skipCombos, bool expected)
    {
        Assert.Equal(expected, DynamicStringSupport.IsSafeContract(new DynamicStringContract { Type = type, Method = method }, skipCombos));
    }

    [Fact(DisplayName = "PrepareMethodParameters splits on top-level commas only")]
    public void PrepareMethodParameters_KeepsGenericArgumentsTogether()
    {
        var parameters = DynamicStringSupport.PrepareMethodParameters(
            "[System.Object，System.Collections.Generic.List<System.String，System.Int32>，System.Boolean]");

        Assert.Equal(["System.Object", "System.Collections.Generic.List<System.String,System.Int32>", "System.Boolean"], parameters);
    }
}
