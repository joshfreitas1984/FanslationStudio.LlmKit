using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace FanslationStudio.LlmKit.Tests.Workflow;

/// <summary>
/// Packaging is the one place a slot's value is put back: <c>{npcName}</c> becomes the game's <c>///a///</c> from the split's
/// <c>tokens</c> alone, a glossary slot reads the glossary as it is at packaging, and a slot that cannot be resolved sends the row
/// to its raw fallback instead of shipping a literal <c>{slot}</c>.
/// </summary>
[Collection("StringTokenReplacerStatics")]
public class TokenPackagingTests : IDisposable
{
    private const string Title = "<少侠||姑娘>";

    private const string ConfigYaml = """
        models:
          - name: Standard
            model: test-model
            url: "http://localhost/test"
        qualityControl:
          enabled: true
          minAcceptableScore: 70
        namedTokenReplacers:
          - literal: "<fName>"
            name: firstName
          - pattern: '<[^<>|]*\|\|[^<>|]*>'
            name: playerTitle
            restoreFromGlossary: true
          - literal: gangName
            name: gangName
            restoreAs: "///gangName///"
          - pattern: '(?:(?<=[^\x00-\x7F])|(?<=[>,.!?:;()])|^)a(?=[^\x00-\x7F]|[<,.!?:;()\[]|$)'
            name: npcName
            restoreAs: "///a///"
        """;

    private static readonly TextFileToSplit File1 = new() { Path = "Dialog.csv", SkipColumns = [0] };

    private readonly string _dir = Directory.CreateTempSubdirectory("llmkit-token-packaging-").FullName;

    public TokenPackagingTests()
    {
        Directory.CreateDirectory($"{_dir}/Converted");
        Directory.CreateDirectory($"{_dir}/Glossary");
        File.WriteAllText($"{_dir}/Config.yaml", ConfigYaml);
        WriteGlossary("<Young Hero||Miss>");
    }

    public void Dispose()
    {
        StringTokenReplacer.SetNamedTokens([], []);
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void WriteGlossary(string result) =>
        File.WriteAllText($"{_dir}/Glossary/Titles.yaml", $"- raw: \"{Title}\"\n  result: \"{result}\"\n");

    private static TranslationSplit S(int split, int subIndex, string text, string translated, params (string Name, string Source)[] tokens)
    {
        var s = new TranslationSplit(split, subIndex, text) { Translated = translated };
        foreach (var (name, source) in tokens)
            s.Tokens[name] = new TokenValue(source);
        return s;
    }

    private void WriteConverted(params TranslationLine[] lines) =>
        File.WriteAllText($"{_dir}/Converted/{File1.Path}.yaml", YamlHelper.CreateSerializer().Serialize(lines.ToList()));

    private async Task<(string[] Lines, (int Passed, int QcRejected, int RawFallback) Counts, List<(int Column, string Raw, string Packaged)> Columns)> PackageAsync(GameHooks? hooks = null)
    {
        var config = ConfigurationExtensions.GetConfiguration(_dir, hooks);
        var columns = new List<(int, string, string)>();
        var counts = await CsvGameDataWorkflow.PackageAsync(_dir, File1, (c, raw, packaged) => columns.Add((c, raw, packaged)), null, config);
        return (File.ReadAllLines($"{_dir}/Mod/{File1.Path}"), counts, columns);
    }

    [Fact(DisplayName = "Packaging restores a copy, a fixed and a glossary slot from the split's tokens")]
    public async Task Packaging_RestoresEveryKind()
    {
        WriteConverted(new TranslationLine
        {
            Raw = $"1,{Title}，<fName>，gangName的a",
            Splits =
            [
                S(1, 0, "{playerTitle}，{firstName}，{gangName}的{npcName}", "{playerTitle}, {firstName}, the {npcName} of {gangName}",
                    ("playerTitle", Title), ("firstName", "<fName>"), ("gangName", "gangName"), ("npcName", "a")),
            ],
        });

        var (lines, counts, _) = await PackageAsync();

        Assert.Equal(["1,\"<Young Hero||Miss>, <fName>, the ///a/// of ///gangName///\""], lines);
        Assert.Equal((1, 0, 0), counts);
    }

    [Fact(DisplayName = "Hooks and callbacks get the source text in the game's form")]
    public async Task Packaging_HooksSeeRestoredSource()
    {
        WriteConverted(new TranslationLine
        {
            Raw = "1,<fName>兄",
            Splits = [S(1, 0, "{firstName}兄", "Brother {firstName}", ("firstName", "<fName>"))],
        });
        var seen = new List<(string Raw, string Result)>();
        var hooks = new GameHooks { CustomPackagingFixup = (_, _, raw, result) => { seen.Add((raw, result)); return result; } };

        var (_, _, columns) = await PackageAsync(hooks);

        Assert.Equal([("<fName>兄", "Brother <fName>")], seen);
        Assert.Equal([(1, "<fName>兄", "Brother <fName>")], columns);
    }

    [Fact(DisplayName = "A glossary change reaches the packaged text with Converted untouched")]
    public async Task Packaging_ReadsTheGlossaryAsItIsNow()
    {
        WriteConverted(new TranslationLine
        {
            Raw = $"1,{Title}好",
            Splits = [S(1, 0, "{playerTitle}好", "Hello {playerTitle}", ("playerTitle", Title))],
        });
        var converted = File.ReadAllText($"{_dir}/Converted/{File1.Path}.yaml");

        Assert.Equal(["1,Hello <Young Hero||Miss>"], (await PackageAsync()).Lines);

        WriteGlossary("<Hero||Heroine>");
        Assert.Equal(["1,Hello <Hero||Heroine>"], (await PackageAsync()).Lines);
        Assert.Equal(converted, File.ReadAllText($"{_dir}/Converted/{File1.Path}.yaml"));
    }

    [Fact(DisplayName = "A slot with no tokens entry sends the row to its raw fallback")]
    public async Task Packaging_MissingTokensEntry_FallsBackToRaw()
    {
        var raw = "1,<fName>兄";
        WriteConverted(new TranslationLine { Raw = raw, Splits = [S(1, 0, "{firstName}兄", "Brother {firstName}")] });

        var (lines, counts, _) = await PackageAsync();

        Assert.Equal([raw], lines);
        Assert.Equal((0, 0, 1), counts);
    }

    [Fact(DisplayName = "A slot the translation invented sends the row to its raw fallback")]
    public async Task Packaging_InventedSlot_FallsBackToRaw()
    {
        var raw = "1,<fName>兄";
        WriteConverted(new TranslationLine { Raw = raw, Splits = [S(1, 0, "{firstName}兄", "{npcName} {firstName}", ("firstName", "<fName>"))] });

        var (lines, counts, _) = await PackageAsync();

        Assert.Equal([raw], lines);
        Assert.Equal((0, 0, 1), counts);
    }

    [Fact(DisplayName = "A glossary slot whose entry is gone falls back to raw instead of shipping the slot")]
    public async Task Packaging_GlossaryEntryGone_FallsBackToRaw()
    {
        var raw = "1,<没有||没有>好";
        WriteConverted(new TranslationLine { Raw = raw, Splits = [S(1, 0, "{playerTitle}好", "Hello {playerTitle}", ("playerTitle", "<没有||没有>"))] });

        var (lines, counts, _) = await PackageAsync();

        Assert.Equal([raw], lines);
        Assert.Equal(1, counts.RawFallback);
    }

    [Fact(DisplayName = "A templated column restores each fragment with its own tokens before rebuilding the cell")]
    public async Task Packaging_TemplatedColumn_RestoresPerFragment()
    {
        WriteConverted(new TranslationLine
        {
            Raw = "1,{0}",
            Templates = [new FieldTemplate(1, "{0};{1}")],
            Splits =
            [
                S(1, 0, "{firstName}好", "Hi {firstName}", ("firstName", "<fName>")),
                S(1, 1, "{npcName}呀", "Oh {npcName}", ("npcName", "a")),
            ],
        });

        var (lines, _, _) = await PackageAsync();

        Assert.Equal(["1,Hi <fName>;Oh ///a///"], lines);
    }

    [Fact(DisplayName = "A token in the template's literal text is restored from the template's tokens")]
    public async Task Packaging_TemplateTokens_AreRestored()
    {
        WriteConverted(new TranslationLine
        {
            Raw = "1,<b>a</b>你好",
            Templates = [new FieldTemplate(1, "<b>{npcName}</b>{0}") { Tokens = new() { ["npcName"] = new TokenValue("a") } }],
            Splits = [S(1, 0, "你好", "Hello")],
        });

        var (lines, counts, _) = await PackageAsync();

        Assert.Equal(["1,<b>///a///</b>Hello"], lines);
        Assert.Equal((1, 0, 0), counts);
    }

    [Fact(DisplayName = "A template slot with no tokens entry sends the row to its raw fallback")]
    public async Task Packaging_TemplateSlotWithoutTokens_FallsBackToRaw()
    {
        var raw = "1,<b>a</b>你好";
        WriteConverted(new TranslationLine
        {
            Raw = raw,
            Templates = [new FieldTemplate(1, "<b>{npcName}</b>{0}")],
            Splits = [S(1, 0, "你好", "Hello")],
        });

        var (lines, counts, _) = await PackageAsync();

        Assert.Equal([raw], lines);
        Assert.Equal((0, 0, 1), counts);
    }

    [Fact(DisplayName = "A whole-cell QC correction restores the template's slots too")]
    public async Task Packaging_QcCorrection_RestoresTemplateSlots()
    {
        var fragment = S(1, 0, "你好", "Hello");
        fragment.QcStatus = QcStatus.Corrected;
        fragment.QcTranslated = "<b>{npcName}</b>Hi";
        fragment.QcQualityScore = 95;
        fragment.QcReviewedText = "<b>{npcName}</b>Hello";
        WriteConverted(new TranslationLine
        {
            Raw = "1,<b>a</b>你好",
            Templates = [new FieldTemplate(1, "<b>{npcName}</b>{0}") { Tokens = new() { ["npcName"] = new TokenValue("a") } }],
            Splits = [fragment],
        });

        Assert.Equal(["1,<b>///a///</b>Hi"], (await PackageAsync()).Lines);
    }

    [Fact(DisplayName = "A fresh whole-cell QC correction is restored from the cell's tokens")]
    public async Task Packaging_QcCorrection_IsRestored()
    {
        var first = S(1, 0, "{firstName}好", "Hi {firstName}", ("firstName", "<fName>"));
        first.QcStatus = QcStatus.Corrected;
        first.QcTranslated = "Hello {firstName}";
        first.QcQualityScore = 95;
        first.QcReviewedText = "Hi {firstName}";
        WriteConverted(new TranslationLine { Raw = "1,<fName>好", Splits = [first] });

        Assert.Equal(["1,Hello <fName>"], (await PackageAsync()).Lines);
    }

    [Fact(DisplayName = "Rows without tokens package exactly as before")]
    public async Task Packaging_TokenFreeRow_IsUnchanged()
    {
        WriteConverted(new TranslationLine { Raw = "1,你好", Splits = [S(1, 0, "你好", "Hello {0} {x}")] });

        Assert.Equal(["1,Hello {0} {x}"], (await PackageAsync()).Lines);
    }
}
