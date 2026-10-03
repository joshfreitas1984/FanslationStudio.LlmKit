using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace FanslationStudio.LlmKit.Tests.Workflow;

/// <summary>
/// Pins the exact packaged output and counts of every Converted -> Mod packaging workflow
/// (<see cref="PrefabTextWorkflow"/>, <see cref="DynamicStringWorkflow"/>,
/// <see cref="CsvGameDataWorkflow"/>, <see cref="JsonGameDataWorkflow"/>) across the field shapes
/// and QC states they share: plain and templated fields, fresh/stale QC, a score-gate rejection,
/// flagged/unsafe/untranslated splits, and the per-workflow quirks (anchor fallback, literal
/// "\n"/U+2011 fixups, label-number spacing, bare-fragment entries, skipped columns).
/// </summary>
public class PackagingParityTests
{
    private const string ConfigYaml = "models:\n  - name: Standard\n    model: test-model\n    url: \"http://localhost/test\"\nqualityReview:\n  enabled: true\n  minAcceptableScore: 70\n";

    private sealed class TempWorkingDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("llmkit-packaging-parity-").FullName;

        public TempWorkingDirectory()
        {
            Directory.CreateDirectory($"{Path}/Converted");
            File.WriteAllText($"{Path}/Config.yaml", ConfigYaml);
        }

        public void WriteConverted(string file, List<TranslationLine> lines) =>
            File.WriteAllText($"{Path}/Converted/{file}.yaml", YamlHelper.CreateSerializer().Serialize(lines));

        public string ReadMod(string file) => File.ReadAllText($"{Path}/Mod/{file}");

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }

    private static TranslationSplit S(int split, int subIndex, string text, string translated) =>
        new(split, subIndex, text) { Translated = translated };

    private static TranslationSplit P(string path, int subIndex, string text, string translated) =>
        new(0, subIndex, text) { SplitPath = path, Translated = translated };

    /// <summary>Marks <paramref name="anchor"/> as QC-reviewed; fresh when <paramref name="reviewedText"/> is the column's current effective text.</summary>
    private static TranslationSplit Qc(TranslationSplit anchor, string qcTranslated, int score, string reviewedText)
    {
        anchor.QcStatus = QcStatus.Corrected;
        anchor.QcTranslated = qcTranslated;
        anchor.QcQualityScore = score;
        anchor.QcReviewedText = reviewedText;
        return anchor;
    }

    private static string Effective(string template, params string[] translated) =>
        CompoundFieldSplitter.Reconstruct(template, [.. translated]);

    private static TranslationLine Plain(string raw, TranslationSplit split) => new() { Raw = raw, Splits = [split] };

    private static TranslationLine Templated(string raw, string template, params TranslationSplit[] fragments) =>
        new() { Raw = raw, Templates = [new FieldTemplate(0, template)], Splits = [.. fragments] };

    private static List<TranslationLine> ColumnZeroLines()
    {
        var flagged = S(0, 0, "山", "Mountain");
        flagged.FlaggedForRetranslation = true;
        var unsafeSplit = S(0, 0, "火", "Fire");
        unsafeSplit.SafeToTranslate = false;
        var flaggedFragment = S(0, 1, "丁", "D");
        flaggedFragment.FlaggedForRetranslation = true;

        return
        [
            Plain("你好", S(0, 0, "你好", "Hello")),
            Plain("世界", Qc(S(0, 0, "世界", "World"), "Earth", 90, "World")),
            Plain("天空", Qc(S(0, 0, "天空", "Sky"), "Heaven", 10, "Sky")),
            Plain("大地", Qc(S(0, 0, "大地", "Land"), "Ground", 90, "Stale")),
            Plain("山", flagged),
            Plain("水", S(0, 0, "水", "")),
            Plain("火", unsafeSplit),
            Plain("风雨", S(0, 0, "风雨", "Wind\\nRain‑x")),
            Plain("意志4", S(0, 0, "意志4", "Will4")),
            Templated("甲：乙", "{0}：{1}", S(0, 0, "甲", "A"), S(0, 1, "乙", "B")),
            Templated("丙：丁", "{0}：{1}", Qc(S(0, 0, "丙", "C"), "C fixed: D", 95, Effective("{0}：{1}", "C", "D")), S(0, 1, "丁", "D")),
            Templated("戊：己", "{0}：{1}", Qc(S(0, 0, "戊", "E"), "E rejected", 10, Effective("{0}：{1}", "E", "F")), S(0, 1, "己", "F")),
            Templated("庚：辛", "{0}：{1}", Qc(S(0, 0, "庚", "G"), "G stale", 95, "Stale"), S(0, 1, "辛", "H")),
            Templated("丙：丁丁", "{0}：{1}", S(0, 0, "丙", "C"), flaggedFragment),
            Templated("壬：癸", "{0}：{1}", S(0, 0, "壬", "I"), S(0, 1, "癸", "")),
            Templated("子丑", "{0}-{1}", Qc(S(0, 1, "子", "Zi"), "Zi-Chou fixed", 95, Effective("{0}-{1}", "Zi", "Chou")), S(0, 2, "丑", "Chou")),
            Templated("打扰了;GovernPlotStart;1", "{0};GovernPlotStart;1", S(0, 0, "打扰了", "Excuse me")),
            new TranslationLine { Raw = "no splits" },
        ];
    }

    [Fact(DisplayName = "PrefabText packaging output and counts are pinned")]
    public async Task PrefabText_PackagedOutputIsPinned()
    {
        using var dir = new TempWorkingDirectory();
        dir.WriteConverted("Prefab.txt", ColumnZeroLines());

        var counts = await PrefabTextWorkflow.PackagePrefabTextAsync(dir.Path, new TextFileToSplit { Path = "Prefab.txt", PackageOutput = true });

        AssertPinned(PrefabExpected, dir.ReadMod("Prefab.txt.yaml"), counts);
    }

    [Fact(DisplayName = "Packaging uses a supplied config instead of reading Config.yaml")]
    public async Task Packaging_UsesSuppliedConfig()
    {
        using var dir = new TempWorkingDirectory();
        File.Delete($"{dir.Path}/Config.yaml");
        dir.WriteConverted("Prefab.txt", ColumnZeroLines());
        dir.WriteConverted("Dynamic.txt", ColumnZeroLines());
        dir.WriteConverted("Data.csv", CsvLines());
        dir.WriteConverted("Data.json", JsonLines());
        var config = new FanslationStudio.LlmKit.Configuration.LlmConfig { QualityReview = new() { Enabled = true, MinAcceptableScore = 70 } };

        var prefab = await PrefabTextWorkflow.PackagePrefabTextAsync(dir.Path, new TextFileToSplit { Path = "Prefab.txt", PackageOutput = true }, config);
        AssertPinned(PrefabExpected, dir.ReadMod("Prefab.txt.yaml"), prefab);

        var dynamic = await DynamicStringWorkflow.PackageDynamicStringsAsync(dir.Path, new TextFileToSplit { Path = "Dynamic.txt", PackageOutput = true }, config);
        AssertPinned(DynamicExpected, dir.ReadMod("Dynamic.txt.yaml"), dynamic);

        var csv = await CsvGameDataWorkflow.PackageAsync(dir.Path, new TextFileToSplit { Path = "Data.csv", PackageOutput = false, SkipColumns = [3] }, config: config);
        AssertPinned(CsvNoOutputExpected, dir.ReadMod("Data.csv"), csv);

        var json = await JsonGameDataWorkflow.PackageAsync(dir.Path, new TextFileToSplit { Path = "Data.json", TextFileType = TextFileType.RawJson, PackageOutput = true }, config);
        AssertPinned(JsonExpected, dir.ReadMod("Data.json"), json);
    }

    [Fact(DisplayName = "PrefabText packaging with PackageOutput disabled is pinned")]
    public async Task PrefabText_PackageOutputDisabledIsPinned()
    {
        using var dir = new TempWorkingDirectory();
        dir.WriteConverted("Prefab.txt", ColumnZeroLines());

        var counts = await PrefabTextWorkflow.PackagePrefabTextAsync(dir.Path, new TextFileToSplit { Path = "Prefab.txt", PackageOutput = false });

        AssertPinned(PrefabNoOutputExpected, dir.ReadMod("Prefab.txt.yaml"), counts);
    }

    [Fact(DisplayName = "DynamicStrings packaging output and counts are pinned")]
    public async Task DynamicStrings_PackagedOutputIsPinned()
    {
        using var dir = new TempWorkingDirectory();
        dir.WriteConverted("Dynamic.txt", ColumnZeroLines());

        var counts = await DynamicStringWorkflow.PackageDynamicStringsAsync(dir.Path, new TextFileToSplit { Path = "Dynamic.txt", PackageOutput = true });

        AssertPinned(DynamicExpected, dir.ReadMod("Dynamic.txt.yaml"), counts);
    }

    private static List<TranslationLine> CsvLines()
    {
        var flaggedFragment = S(2, 1, "乙", "B");
        flaggedFragment.FlaggedForRetranslation = true;
        var unsafeSplit = S(1, 0, "你好", "Hello");
        unsafeSplit.SafeToTranslate = false;

        return
        [
            new() { Raw = "1,你好,甲：乙", Templates = [new FieldTemplate(2, "{0}：{1}")], Splits = [S(1, 0, "你好", "Hello"), S(2, 0, "甲", "A"), S(2, 1, "乙", "B")] },
            new() { Raw = "2,世界,丙：丁", Templates = [new FieldTemplate(2, "{0}：{1}")], Splits = [Qc(S(1, 0, "世界", "World"), "Earth", 90, "World"), Qc(S(2, 0, "丙", "C"), "C fixed: D", 95, Effective("{0}：{1}", "C", "D")), S(2, 1, "丁", "D")] },
            new() { Raw = "3,天空,戊：己", Templates = [new FieldTemplate(2, "{0}：{1}")], Splits = [Qc(S(1, 0, "天空", "Sky"), "Heaven", 10, "Sky"), Qc(S(2, 0, "戊", "E"), "E rejected", 10, Effective("{0}：{1}", "E", "F")), S(2, 1, "己", "F")] },
            new() { Raw = "4,大地,庚：辛", Templates = [new FieldTemplate(2, "{0}：{1}")], Splits = [Qc(S(1, 0, "大地", "Land"), "Ground", 90, "Stale"), Qc(S(2, 0, "庚", "G"), "G stale", 95, "Stale"), S(2, 1, "辛", "H")] },
            new() { Raw = "5,你好,甲：乙", Templates = [new FieldTemplate(2, "{0}：{1}")], Splits = [S(1, 0, "你好", "Hello"), S(2, 0, "甲", "A"), flaggedFragment] },
            new() { Raw = "6,水,甲", Splits = [S(1, 0, "水", ""), S(2, 0, "甲", "A")] },
            new() { Raw = "7,,甲", Splits = [S(1, 0, "", ""), S(2, 0, "甲", "A")] },
            new() { Raw = "8,你好,x,技能：等级", Templates = [new FieldTemplate(3, "{0}：{1}")], Splits = [S(1, 0, "你好", "Hello"), S(3, 0, "技能", "Skill"), S(3, 1, "等级", "Level")] },
            new() { Raw = "9,子丑", Templates = [new FieldTemplate(1, "{0}-{1}")], Splits = [Qc(S(1, 1, "子", "Zi"), "Zi-Chou fixed", 95, Effective("{0}-{1}", "Zi", "Chou")), S(1, 2, "丑", "Chou")] },
            new() { Raw = "10,你好", Splits = [unsafeSplit] },
            new() { Raw = "11,\"你,好\",风雨", Splits = [S(1, 0, "你,好", "Hello, there"), S(2, 0, "风雨", "Wind\\nRain‑x")] },
        ];
    }

    [Fact(DisplayName = "CSV packaging output, counts and column callbacks are pinned")]
    public async Task Csv_PackagedOutputIsPinned()
    {
        using var dir = new TempWorkingDirectory();
        dir.WriteConverted("Data.csv", CsvLines());

        var packagedColumns = new List<string>();
        var counts = await CsvGameDataWorkflow.PackageAsync(dir.Path, new TextFileToSplit { Path = "Data.csv", PackageOutput = true, SkipColumns = [3] },
            onColumnPackaged: (column, raw, packaged) => packagedColumns.Add($"{column}|{raw}|{packaged}"));

        AssertPinned(CsvExpected, dir.ReadMod("Data.csv") + "\n---\n" + string.Join("\n", packagedColumns), counts);
    }

    [Fact(DisplayName = "CSV packaging with PackageOutput disabled is pinned")]
    public async Task Csv_PackageOutputDisabledIsPinned()
    {
        using var dir = new TempWorkingDirectory();
        dir.WriteConverted("Data.csv", CsvLines());

        var counts = await CsvGameDataWorkflow.PackageAsync(dir.Path, new TextFileToSplit { Path = "Data.csv", PackageOutput = false, SkipColumns = [3] });

        AssertPinned(CsvNoOutputExpected, dir.ReadMod("Data.csv"), counts);
    }

    private static List<TranslationLine> JsonLines()
    {
        var flagged = Qc(P("Name", 0, "山", "Mountain"), "Peak", 90, "Mountain");
        flagged.FlaggedForRetranslation = true;
        var flaggedFragment = P("Desc", 1, "乙", "B");
        flaggedFragment.FlaggedForRetranslation = true;
        static FieldTemplate T(string path, string template) => new(0, template) { SplitPath = path };

        return
        [
            new() { RawIndex = "1", Raw = """{"Key":1,"Name":"你好","Desc":"甲：乙","Num":5}""", Templates = [T("Desc", "{0}：{1}")], Splits = [P("Name", 0, "你好", "Hello"), P("Desc", 0, "甲", "A"), P("Desc", 1, "乙", "B")] },
            new() { RawIndex = "2", Raw = """{"Key":2,"Name":"世界","Desc":"丙：丁"}""", Templates = [T("Desc", "{0}：{1}")], Splits = [Qc(P("Name", 0, "世界", "World"), "Earth", 90, "World"), Qc(P("Desc", 0, "丙", "C"), "C fixed: D", 95, Effective("{0}：{1}", "C", "D")), P("Desc", 1, "丁", "D")] },
            new() { RawIndex = "3", Raw = """{"Key":3,"Name":"天空","Desc":"戊：己"}""", Templates = [T("Desc", "{0}：{1}")], Splits = [Qc(P("Name", 0, "天空", "Sky"), "Heaven", 10, "Sky"), Qc(P("Desc", 0, "戊", "E"), "E rejected", 10, Effective("{0}：{1}", "E", "F")), P("Desc", 1, "己", "F")] },
            new() { RawIndex = "4", Raw = """{"Key":4,"Name":"大地","Desc":"庚：辛"}""", Templates = [T("Desc", "{0}：{1}")], Splits = [Qc(P("Name", 0, "大地", "Land"), "Ground", 90, "Stale"), Qc(P("Desc", 0, "庚", "G"), "G stale", 95, "Stale"), P("Desc", 1, "辛", "H")] },
            new() { RawIndex = "5", Raw = """{"Key":5,"Name":"山","Desc":"甲：乙"}""", Templates = [T("Desc", "{0}：{1}")], Splits = [flagged, P("Desc", 0, "甲", "A"), flaggedFragment] },
            new() { RawIndex = "abc", Raw = """{"Key":"abc","Name":"水","List":["你","好"]}""", Splits = [P("Name", 0, "水", ""), P("List[0]", 0, "你", "You"), P("List[1]", 0, "好", "Good\\n‑")] },
            new() { RawIndex = "7", Raw = """{"Key":7,"Desc":"子丑"}""", Templates = [T("Desc", "{0}-{1}")], Splits = [Qc(P("Desc", 1, "子", "Zi"), "Zi-Chou fixed", 95, Effective("{0}-{1}", "Zi", "Chou")), P("Desc", 2, "丑", "Chou")] },
        ];
    }

    [Fact(DisplayName = "JSON packaging output and counts are pinned")]
    public async Task Json_PackagedOutputIsPinned()
    {
        using var dir = new TempWorkingDirectory();
        dir.WriteConverted("Data.json", JsonLines());

        var counts = await JsonGameDataWorkflow.PackageAsync(dir.Path, new TextFileToSplit { Path = "Data.json", TextFileType = TextFileType.RawJson, PackageOutput = true });

        AssertPinned(JsonExpected, dir.ReadMod("Data.json"), counts);
    }

    private static void AssertPinned(string expected, string output, (int, int, int) counts) =>
        Assert.Equal(expected, $"{counts}\n{output.Replace("\r\n", "\n")}");

    private const string PrefabExpected = """
(10, 2, 5)
- raw: "你好"
  result: "Hello"
- raw: "世界"
  result: "Earth"
- raw: "天空"
  result: "Sky"
- raw: "大地"
  result: "Land"
- raw: "风雨"
  result: "Wind\nRain-x"
- raw: "意志4"
  result: "Will4"
- raw: "甲：乙"
  result: "A：B"
- raw: "丙：丁"
  result: "C fixed: D"
- raw: "戊：己"
  result: "E：F"
- raw: "庚：辛"
  result: "G：H"
- raw: "子丑"
  result: "Zi-Chou"
- raw: "打扰了;GovernPlotStart;1"
  result: "Excuse me;GovernPlotStart;1"

""";

    private const string PrefabNoOutputExpected = """
(6, 1, 10)
- raw: "你好"
  result: "Hello"
- raw: "世界"
  result: "Earth"
- raw: "天空"
  result: "Sky"
- raw: "大地"
  result: "Land"
- raw: "风雨"
  result: "Wind\nRain-x"
- raw: "意志4"
  result: "Will4"
- raw: "丙：丁"
  result: "C fixed: D"

""";

    private const string DynamicExpected = """
(10, 2, 5)
- raw: "你好"
  result: "Hello"
- raw: "世界"
  result: "Earth"
- raw: "天空"
  result: "Sky"
- raw: "大地"
  result: "Land"
- raw: "风雨"
  result: "Wind\nRain-x"
- raw: "意志4"
  result: "Will 4"
- raw: "甲：乙"
  result: "A：B"
- raw: "丙：丁"
  result: "C fixed: D"
- raw: "戊：己"
  result: "E：F"
- raw: "庚：辛"
  result: "G：H"
- raw: "子丑"
  result: "Zi-Chou"
- raw: "打扰了;GovernPlotStart;1"
  result: "Excuse me;GovernPlotStart;1"
- raw: "打扰了"
  result: "Excuse me"

""";

    private const string CsvExpected = """
(8, 0, 3)
1,Hello,A：B
2,Earth,C fixed: D
3,Sky,E：F
4,Land,G：H
5,你好,甲：乙
6,水,甲
7,,A
8,Hello,x,技能：等级
9,Zi-Chou fixed
10,你好
11,"Hello, there","Wind
Rain-x"

---
2|甲乙|A：B
1|你好|Hello
2|丙|C fixed: D
1|世界|Earth
2|戊己|E：F
1|天空|Sky
2|庚辛|G：H
1|大地|Land
2|甲|A
1|你好|Hello
1|子|Zi-Chou fixed
1|你,好|Hello, there
2|风雨|Wind
Rain-x
""";

    private const string CsvNoOutputExpected = """
(1, 0, 10)
1,你好,甲：乙
2,世界,丙：丁
3,天空,戊：己
4,大地,庚：辛
5,你好,甲：乙
6,水,甲
7,,甲
8,你好,x,技能：等级
9,Zi-Chou fixed
10,你好
11,"你,好",风雨

""";

    private const string JsonExpected = """
(12, 0, 2)
[
  {
    "Key": 1,
    "Name": "Hello",
    "Desc": "A：B",
    "Num": 5
  },
  {
    "Key": 2,
    "Name": "Earth",
    "Desc": "C fixed: D"
  },
  {
    "Key": 3,
    "Name": "Sky",
    "Desc": "E：F"
  },
  {
    "Key": 4,
    "Name": "Land",
    "Desc": "G：H"
  },
  {
    "Key": 5,
    "Name": "Peak",
    "Desc": "甲：乙"
  },
  {
    "Key": "abc",
    "Name": "水",
    "List": [
      "You",
      "Good\n-"
    ]
  },
  {
    "Key": 7,
    "Desc": "Zi-Chou fixed"
  }
]
""";

}
