using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace FanslationStudio.LlmKit.Tests.Workflow;

/// <summary>
/// Covers <see cref="JsonGameDataWorkflow"/> against this game's actual JSON shape (see
/// Files/Raw/Dumped/Hero.json, Skill.json, Dice.json in WanXiangOverLlm): loose/missing
/// properties, "...Tw"/"...Final" sibling skipping, array-element SplitPaths, and a round-trip
/// export -> package fixture including a compound (multi-fragment) field.
/// </summary>
public class JsonGameDataWorkflowTests
{
    private static string CreateWorkingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "JsonGameDataWorkflowTests_" + Guid.NewGuid());
        Directory.CreateDirectory($"{dir}/Raw/Dumped");

        // Minimal Config.yaml - just enough for ConfigurationExtensions.GetConfiguration to succeed
        // (PackageAsync reads QualityReview.MinAcceptableScore from it, defaulted to 70).
        File.WriteAllText($"{dir}/Config.yaml", "models:\n  - name: Standard\n    model: test-model\n    url: \"http://localhost/test\"\n");

        return dir;
    }

    private static TextFileToSplit TestFile(string path) => new()
    {
        Path = path,
        TextFileType = TextFileType.RawJson,
        PackageOutput = true,
    };

    [Fact(DisplayName = "Export handles loose/missing properties and skips Tw/Final siblings")]
    public void ExportToCustomFormat_LooseSchemaAndSiblingSkipping()
    {
        var dir = CreateWorkingDirectory();
        try
        {
            var json = """
            [
              { "Key": 1, "Name": "你好世界", "NameTw": "你好世界(繁)" },
              { "Key": 2, "Desc": "测试内容", "DescFinal": "should be skipped" }
            ]
            """;
            File.WriteAllText($"{dir}/Raw/Dumped/Test.json", json);

            var textFile = TestFile("Test.json");
            JsonGameDataWorkflow.ExportToCustomFormat(dir, textFile);

            var lines = YamlHelper.CreateDeserializer()
                .Deserialize<List<TranslationLine>>(File.ReadAllText($"{dir}/Raw/Export/Test.json.yaml"));

            Assert.Equal(2, lines.Count);

            var line1 = lines.Single(l => l.RawIndex == "1");
            Assert.Single(line1.Splits);
            Assert.Equal("Name", line1.Splits[0].SplitPath);
            Assert.Equal("你好世界", line1.Splits[0].Text);

            var line2 = lines.Single(l => l.RawIndex == "2");
            Assert.Single(line2.Splits);
            Assert.Equal("Desc", line2.Splits[0].SplitPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact(DisplayName = "Export addresses string-array elements by SplitPath, skipping non-Chinese entries")]
    public void ExportToCustomFormat_ArrayElementSplitPaths()
    {
        var dir = CreateWorkingDirectory();
        try
        {
            var json = """
            [
              { "Key": 3, "ChatList": ["你好啊", "nil", "再见"] }
            ]
            """;
            File.WriteAllText($"{dir}/Raw/Dumped/Test.json", json);

            JsonGameDataWorkflow.ExportToCustomFormat(dir, TestFile("Test.json"));

            var lines = YamlHelper.CreateDeserializer()
                .Deserialize<List<TranslationLine>>(File.ReadAllText($"{dir}/Raw/Export/Test.json.yaml"));

            var line = lines.Single();
            Assert.Equal(2, line.Splits.Count);
            Assert.Contains(line.Splits, s => s.SplitPath == "ChatList[0]" && s.Text == "你好啊");
            Assert.Contains(line.Splits, s => s.SplitPath == "ChatList[2]" && s.Text == "再见");
            Assert.DoesNotContain(line.Splits, s => s.SplitPath == "ChatList[1]");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact(DisplayName = "Round-trip export -> translate -> package reconstructs compound fields, preserves untouched data, and isolates per-field failures")]
    public async Task ExportThenPackage_RoundTrip()
    {
        var dir = CreateWorkingDirectory();
        try
        {
            var json = """
            [
              {
                "Key": 9001,
                "Name": "王小雄",
                "NameTw": "王小雄",
                "Desc": "你好;再见",
                "DescTw": "你好；再見",
                "Level": 5,
                "ChatList": ["加油啊", "nil"]
              }
            ]
            """;
            File.WriteAllText($"{dir}/Raw/Dumped/Hero.json", json);

            var textFile = TestFile("Hero.json");
            JsonGameDataWorkflow.ExportToCustomFormat(dir, textFile);

            var deserializer = YamlHelper.CreateDeserializer();
            var lines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText($"{dir}/Converted/Hero.json.yaml"));
            var line = lines.Single();

            // "Desc" ("你好;再见") is a compound field - two fragments joined by a literal ';'.
            Assert.Single(line.Templates);
            Assert.Equal("Desc", line.Templates[0].SplitPath);

            var descFragments = line.Splits.Where(s => s.SplitPath == "Desc").OrderBy(s => s.SubIndex).ToList();
            Assert.Equal(2, descFragments.Count);
            descFragments[0].Translated = "Hello";
            descFragments[1].Translated = "Goodbye";

            // "Name" is a plain (trivial) single-fragment field.
            var nameSplit = line.Splits.Single(s => s.SplitPath == "Name");
            nameSplit.Translated = "Little Hero Wang";

            // "ChatList[0]" translates fine; a second array-element field is deliberately left
            // untranslated/flagged to prove one field failing doesn't discard the rest of the object.
            var chatSplit = line.Splits.Single(s => s.SplitPath == "ChatList[0]");
            chatSplit.Translated = "Go for it!";

            File.WriteAllText($"{dir}/Converted/Hero.json.yaml", YamlHelper.CreateSerializer().Serialize(lines));

            var (passed, failed) = await JsonGameDataWorkflow.PackageAsync(dir, textFile);

            Assert.Equal(3, passed); // Name, Desc, ChatList[0]
            Assert.Equal(0, failed);

            var output = System.Text.Json.JsonDocument.Parse(File.ReadAllText($"{dir}/Mod/Hero.json"));
            var obj = output.RootElement[0];

            Assert.Equal(9001, obj.GetProperty("Key").GetInt32());
            Assert.Equal("Little Hero Wang", obj.GetProperty("Name").GetString());
            Assert.Equal("Hello;Goodbye", obj.GetProperty("Desc").GetString());
            Assert.Equal("Go for it!", obj.GetProperty("ChatList")[0].GetString());

            // Untouched data survives byte-for-byte: numeric field, Tw siblings (never split), and
            // the array's second ("nil") element that had no Chinese to translate.
            Assert.Equal(5, obj.GetProperty("Level").GetInt32());
            Assert.Equal("王小雄", obj.GetProperty("NameTw").GetString());
            Assert.Equal("你好；再見", obj.GetProperty("DescTw").GetString());
            Assert.Equal("nil", obj.GetProperty("ChatList")[1].GetString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact(DisplayName = "Package falls back to original text for a single failed field without discarding other translated fields on the same object")]
    public async Task PackageAsync_PerFieldFailureIsolation()
    {
        var dir = CreateWorkingDirectory();
        try
        {
            var json = """
            [
              { "Key": 42, "Name": "你好", "Desc": "未翻译" }
            ]
            """;
            File.WriteAllText($"{dir}/Raw/Dumped/Test.json", json);

            var textFile = TestFile("Test.json");
            JsonGameDataWorkflow.ExportToCustomFormat(dir, textFile);

            var deserializer = YamlHelper.CreateDeserializer();
            var lines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText($"{dir}/Converted/Test.json.yaml"));
            var line = lines.Single();

            // Translate Name; leave Desc untranslated (its Translated stays empty with Text set,
            // which CsvGameDataWorkflow-style logic treats as a failure for that field).
            line.Splits.Single(s => s.SplitPath == "Name").Translated = "Hello";

            File.WriteAllText($"{dir}/Converted/Test.json.yaml", YamlHelper.CreateSerializer().Serialize(lines));

            var (passed, failed) = await JsonGameDataWorkflow.PackageAsync(dir, textFile);

            Assert.Equal(1, passed);
            Assert.Equal(1, failed);

            var output = System.Text.Json.JsonDocument.Parse(File.ReadAllText($"{dir}/Mod/Test.json"));
            var obj = output.RootElement[0];

            Assert.Equal("Hello", obj.GetProperty("Name").GetString());
            Assert.Equal("未翻译", obj.GetProperty("Desc").GetString()); // fell back to original raw text
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
