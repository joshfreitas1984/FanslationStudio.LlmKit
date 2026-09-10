using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

/// <summary>
/// Covers <see cref="DynamicStringsCecilWorkflow"/> - the older Mono/Cecil dump+patch format for
/// <see cref="TextFileType.DynamicStrings"/> (5 naive comma-split fields:
/// Type,Method,ILOffset,RawText,ParamTypesList), distinct from <see cref="DynamicStringWorkflow"/>'s
/// IL2CPP substring-dictionary approach.
/// </summary>
public class DynamicStringsCecilWorkflowTests
{
    private static TextFileToSplit TestFile() => new() { Path = "dynamicStrings.txt" };

    private sealed class TempWorkingDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("llmkit-dynstr-tests-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }

    [Fact(DisplayName = "Export splits the Chinese RawText field and strips surrounding quotes")]
    public void ExportDynamicStringsToCustomFormat_ExtractsRawTextField()
    {
        using var dir = new TempWorkingDirectory();
        Directory.CreateDirectory($"{dir.Path}/Raw/Dumped");
        File.WriteAllLines($"{dir.Path}/Raw/Dumped/dynamicStrings.txt",
        [
            "GameTools,Save,88,\"备份失败: \",[System.Object，System.String，System.String，System.Boolean]",
        ]);

        DynamicStringsCecilWorkflow.ExportDynamicStringsToCustomFormat(dir.Path, TestFile());

        var exportYaml = File.ReadAllText($"{dir.Path}/Raw/Export/dynamicStrings.txt.yaml");
        var lines = YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(exportYaml);

        var line = Assert.Single(lines);
        var split = Assert.Single(line.Splits);
        Assert.Equal(3, split.Split);
        Assert.Equal("备份失败: ", split.Text);

        Assert.True(File.Exists($"{dir.Path}/Converted/dynamicStrings.txt.yaml"));
    }

    [Fact(DisplayName = "Export reads from a custom rawSubfolder when given one")]
    public void ExportDynamicStringsToCustomFormat_UsesCustomRawSubfolder()
    {
        using var dir = new TempWorkingDirectory();
        Directory.CreateDirectory($"{dir.Path}/CustomRaw");
        File.WriteAllLines($"{dir.Path}/CustomRaw/dynamicStrings.txt",
        [
            "GameTools,Save,88,备份失败,[]",
        ]);

        DynamicStringsCecilWorkflow.ExportDynamicStringsToCustomFormat(dir.Path, TestFile(), rawSubfolder: "CustomRaw");

        Assert.True(File.Exists($"{dir.Path}/Raw/Export/dynamicStrings.txt.yaml"));
    }

    [Fact(DisplayName = "Package builds a DynamicStringContract for a safe, translated line")]
    public async Task PackageDynamicStringsCecilAsync_PackagesSafeTranslatedLine()
    {
        using var dir = new TempWorkingDirectory();
        Directory.CreateDirectory($"{dir.Path}/Converted");

        var line = new TranslationLine
        {
            Raw = "GameTools,Save,88,备份失败: ,[System.Object，System.String]",
            Splits = [new TranslationSplit(0, "备份失败: ") { Translated = "Backup failed: " }],
        };

        var serializer = YamlHelper.CreateSerializer();
        File.WriteAllText($"{dir.Path}/Converted/dynamicStrings.txt.yaml", serializer.Serialize(new List<TranslationLine> { line }));

        var (passed, failed) = await DynamicStringsCecilWorkflow.PackageDynamicStringsCecilAsync(dir.Path, TestFile());

        Assert.Equal(1, passed);
        Assert.Equal(0, failed);

        var modYaml = File.ReadAllText($"{dir.Path}/Mod/dynamicStrings.txt.yaml");
        var contracts = YamlHelper.CreateDeserializer().Deserialize<List<SharedAssembly.DynamicStrings.DynamicStringContract>>(modYaml);

        var contract = Assert.Single(contracts);
        Assert.Equal("GameTools", contract.Type);
        Assert.Equal("Save", contract.Method);
        Assert.Equal(88, contract.ILOffset);
        Assert.Equal("备份失败: ", contract.Raw);
        Assert.Equal("Backup failed: ", contract.Translation);
        Assert.Equal(["System.Object", "System.String"], contract.Parameters);
    }

    [Fact(DisplayName = "Package skips a line flagged for retranslation and counts it as failed")]
    public async Task PackageDynamicStringsCecilAsync_FlaggedForRetranslation_CountsAsFailed()
    {
        using var dir = new TempWorkingDirectory();
        Directory.CreateDirectory($"{dir.Path}/Converted");

        var line = new TranslationLine
        {
            Raw = "GameTools,Save,88,备份失败: ,[]",
            Splits = [new TranslationSplit(0, "备份失败: ") { Translated = "Backup failed: ", FlaggedForRetranslation = true }],
        };

        var serializer = YamlHelper.CreateSerializer();
        File.WriteAllText($"{dir.Path}/Converted/dynamicStrings.txt.yaml", serializer.Serialize(new List<TranslationLine> { line }));

        var (passed, failed) = await DynamicStringsCecilWorkflow.PackageDynamicStringsCecilAsync(dir.Path, TestFile());

        Assert.Equal(0, passed);
        Assert.Equal(1, failed);
    }

    [Fact(DisplayName = "Package excludes a contract whose Type is on the unsafe skip list, without counting it as failed")]
    public async Task PackageDynamicStringsCecilAsync_UnsafeType_ExcludedWithoutFailure()
    {
        using var dir = new TempWorkingDirectory();
        Directory.CreateDirectory($"{dir.Path}/Converted");

        // "GmManager" is in DynamicStringSupport.IsSafeContract's skipTypes list.
        var line = new TranslationLine
        {
            Raw = "GmManager,DoThing,1,你好,[]",
            Splits = [new TranslationSplit(0, "你好") { Translated = "Hello" }],
        };

        var serializer = YamlHelper.CreateSerializer();
        File.WriteAllText($"{dir.Path}/Converted/dynamicStrings.txt.yaml", serializer.Serialize(new List<TranslationLine> { line }));

        var (passed, failed) = await DynamicStringsCecilWorkflow.PackageDynamicStringsCecilAsync(dir.Path, TestFile());

        Assert.Equal(0, passed);
        Assert.Equal(0, failed);

        var modYaml = File.ReadAllText($"{dir.Path}/Mod/dynamicStrings.txt.yaml");
        var contracts = YamlHelper.CreateDeserializer().Deserialize<List<SharedAssembly.DynamicStrings.DynamicStringContract>>(modYaml);
        Assert.Empty(contracts);
    }
}
