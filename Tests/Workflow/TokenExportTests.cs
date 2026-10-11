using System.Text.RegularExpressions;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace FanslationStudio.LlmKit.Tests;

/// <summary>
/// Export writes slotted text and a per-split <c>tokens</c> map when <c>namedTokenReplacers</c> is configured; a re-export is
/// idempotent; merge keeps translations but takes the tokens from the export; data written before <c>tokens</c> existed still loads.
/// </summary>
[Collection("StringTokenReplacerStatics")]
public class TokenExportTests : IDisposable
{
    private const string Title = "<少侠||姑娘>";
    private const string OtherTitle = "<公子||姑娘>";

    private static readonly CompoundFieldSplitterOptions Options = new()
    {
        PlaceholderPatterns = [new Regex("<fName>"), new Regex(@"<[^<>|]*\|\|[^<>|]*>")],
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TokenExportTests_" + Guid.NewGuid());

    private static readonly TextFileToSplit File1 = new() { Path = "Dialog.csv", SkipColumns = [0] };

    public TokenExportTests()
    {
        Directory.CreateDirectory($"{_dir}/Raw/Dumped/GameData");
        StringTokenReplacer.SetNamedTokens(
        [
            new NamedTokenReplacer { Literal = "<fName>", Name = "firstName" },
            new NamedTokenReplacer { Pattern = @"<[^<>|]*\|\|[^<>|]*>", Name = "playerTitle", RestoreFromGlossary = true },
            new NamedTokenReplacer { Pattern = "(?<=>)a(?=<)", Name = "npcName", RestoreAs = "///a///" },
        ],
        [new GlossaryLine(Title, "<Young Hero||Miss>"), new GlossaryLine(OtherTitle, "<Young Master||Miss>")]);
    }

    public void Dispose()
    {
        StringTokenReplacer.SetNamedTokens([], []);
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private void WriteDump(params string[] lines) =>
        File.WriteAllLines($"{_dir}/Raw/Dumped/GameData/{File1.Path}", lines);

    private List<TranslationLine> ReadExport() =>
        YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(File.ReadAllText($"{_dir}/Raw/Export/{File1.Path}.yaml"));

    [Fact(DisplayName = "Export writes slotted text and the token map")]
    public void Export_SlotsFragmentsAndWritesTokens()
    {
        WriteDump($"1,<fName>兄，你好吗？", $"2,{Title}，请留步");

        CsvGameDataWorkflow.ExportToCustomFormat(_dir, File1, Options);

        var lines = ReadExport();
        Assert.Equal("{firstName}兄，你好吗？", lines[0].Splits[0].Text);
        Assert.Equal("<fName>", lines[0].Splits[0].Tokens["firstName"].Source);
        Assert.Equal("{playerTitle}，请留步", lines[1].Splits[0].Text);
        Assert.Equal(Title, lines[1].Splits[0].Tokens["playerTitle"].Source);
        Assert.Equal("1,<fName>兄，你好吗？", lines[0].Raw);
    }

    [Fact(DisplayName = "A token in the template's literal text is slotted, with its values on the template")]
    public void Export_SlotsTokensInTheTemplate()
    {
        WriteDump("1,<b>a</b>你好，<fName>");

        CsvGameDataWorkflow.ExportToCustomFormat(_dir, File1, Options);

        var line = ReadExport()[0];
        Assert.Equal("<b>{npcName}</b>{0}", line.Templates[0].Template);
        Assert.Equal("a", line.Templates[0].Tokens["npcName"].Source);
        Assert.Equal("你好，{firstName}", line.Splits[0].Text);
        Assert.Equal(["firstName"], line.Splits[0].Tokens.Keys);
        Assert.Equal("1,<b>a</b>你好，<fName>", line.Raw);
    }

    [Fact(DisplayName = "A template with no tokens writes none")]
    public void Export_TemplateWithoutTokens_WritesNone()
    {
        WriteDump("1,<b>好</b>你好");

        CsvGameDataWorkflow.ExportToCustomFormat(_dir, File1, Options);

        Assert.Empty(ReadExport()[0].Templates[0].Tokens);
        Assert.DoesNotContain("tokens", File.ReadAllText($"{_dir}/Raw/Export/{File1.Path}.yaml"));
    }

    [Fact(DisplayName = "Tokens are written to the file only when there are some")]
    public void Export_OmitsEmptyTokens()
    {
        WriteDump("1,你好吗？", "2,<fName>兄");

        CsvGameDataWorkflow.ExportToCustomFormat(_dir, File1, Options);

        var yaml = File.ReadAllText($"{_dir}/Raw/Export/{File1.Path}.yaml");
        var plain = yaml[..yaml.IndexOf("- raw: \"2,", StringComparison.Ordinal)];
        Assert.DoesNotContain("tokens", plain);
        Assert.Contains("tokens:", yaml);
        Assert.Contains("source: \"<fName>\"", yaml);
    }

    [Fact(DisplayName = "A game with no namedTokenReplacers exports byte for byte as before")]
    public void Export_WithoutTokens_IsUnchanged()
    {
        WriteDump("1,<fName>兄，你好吗？", $"2,{Title}，请留步");

        CsvGameDataWorkflow.ExportToCustomFormat(_dir, File1, Options);
        var withTokens = File.ReadAllText($"{_dir}/Raw/Export/{File1.Path}.yaml");

        StringTokenReplacer.SetNamedTokens([], []);
        CsvGameDataWorkflow.ExportToCustomFormat(_dir, File1, Options);
        var without = File.ReadAllText($"{_dir}/Raw/Export/{File1.Path}.yaml");

        Assert.Contains("text: \"<fName>兄，你好吗？\"", without);
        Assert.DoesNotContain("tokens", without);
        Assert.NotEqual(withTokens, without);
    }

    [Fact(DisplayName = "Slot names are numbered across the fragments of one cell")]
    public void Export_NumbersSlotsAcrossFragments()
    {
        WriteDump($"1,{Title}好;{OtherTitle}呀;{Title}吧");

        CsvGameDataWorkflow.ExportToCustomFormat(_dir, File1, Options);

        var splits = ReadExport()[0].Splits;
        Assert.Equal(["{playerTitle}好", "{playerTitle2}呀", "{playerTitle}吧"], splits.Select(s => s.Text));
        Assert.Equal(["playerTitle"], splits[0].Tokens.Keys);
        Assert.Equal(["playerTitle2"], splits[1].Tokens.Keys);
        Assert.Equal(OtherTitle, splits[1].Tokens["playerTitle2"].Source);
    }

    [Fact(DisplayName = "Re-exporting is idempotent and merging changes nothing")]
    public async Task ReExport_IsIdempotent()
    {
        WriteDump("1,<fName>兄，你好吗？", $"2,{Title}，请留步", "3,没有标记");

        CsvGameDataWorkflow.ExportToCustomFormat(_dir, File1, Options);
        var firstExport = File.ReadAllText($"{_dir}/Raw/Export/{File1.Path}.yaml");
        var firstConverted = File.ReadAllText($"{_dir}/Converted/{File1.Path}.yaml");

        CsvGameDataWorkflow.ExportToCustomFormat(_dir, File1, Options);
        await GameFileHandlingBase.MergeFilesIntoTranslatedAsync(_dir, [File1]);

        Assert.Equal(firstExport, File.ReadAllText($"{_dir}/Raw/Export/{File1.Path}.yaml"));
        Assert.Equal(firstConverted, File.ReadAllText($"{_dir}/Converted/{File1.Path}.yaml"));
    }

    [Fact(DisplayName = "Merge keeps the translation and takes the tokens from the export, not from the old data")]
    public async Task Merge_RefreshesTokensFromTheExport()
    {
        WriteDump($"1,{Title}，请留步");
        CsvGameDataWorkflow.ExportToCustomFormat(_dir, File1, Options);

        // The converted file was translated against a different source for the same slotted text.
        var converted = YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(File.ReadAllText($"{_dir}/Converted/{File1.Path}.yaml"));
        converted[0].Splits[0].Translated = "{playerTitle}, please wait";
        converted[0].Splits[0].Tokens = new() { ["playerTitle"] = new TokenValue(OtherTitle) };
        File.WriteAllText($"{_dir}/Converted/{File1.Path}.yaml", YamlHelper.CreateSerializer().Serialize(converted));

        await GameFileHandlingBase.MergeFilesIntoTranslatedAsync(_dir, [File1]);

        var merged = YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(File.ReadAllText($"{_dir}/Converted/{File1.Path}.yaml"));
        Assert.Equal("{playerTitle}, please wait", merged[0].Splits[0].Translated);
        Assert.Equal(Title, merged[0].Splits[0].Tokens["playerTitle"].Source);
    }

    [Fact(DisplayName = "Data written before tokens existed still loads, with an empty map")]
    public void OldData_LoadsWithoutTokens()
    {
        const string yaml = """
            - raw: "1,你好"
              splits:
              - split: 1
                text: "你好"
                translated: "Hello"
            """;

        var split = YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(yaml)[0].Splits[0];

        Assert.NotNull(split.Tokens);
        Assert.Empty(split.Tokens);
        Assert.DoesNotContain("tokens", YamlHelper.CreateSerializer().Serialize(new List<TranslationSplit> { split }));
    }

    [Fact(DisplayName = "A token map round-trips through the YAML")]
    public void Tokens_RoundTripThroughYaml()
    {
        var split = new TranslationSplit(1, "{npcName}说") { Tokens = new() { ["npcName"] = new TokenValue("a") } };

        var yaml = YamlHelper.CreateSerializer().Serialize(new List<TranslationSplit> { split });
        var back = YamlHelper.CreateDeserializer().Deserialize<List<TranslationSplit>>(yaml)[0];

        Assert.Contains("npcName:", yaml);
        Assert.Equal("a", back.Tokens["npcName"].Source);
    }
}
