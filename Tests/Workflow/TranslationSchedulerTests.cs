using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

/// <summary>
/// End-to-end runs of both translation schedulers against a temp workspace whose every string is
/// satisfied by ManualTranslations.yaml - exercises dedup, duplicate propagation, cache fill and
/// write-back without any LLM call.
/// </summary>
public class TranslationSchedulerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sched-{Guid.NewGuid():N}");
    private readonly TextFileToSplit[] _files = [new() { Path = "A.txt" }, new() { Path = "B.txt" }];

    public TranslationSchedulerTests()
    {
        Directory.CreateDirectory($"{_dir}/Raw/Export");
        Directory.CreateDirectory($"{_dir}/TestResults/OldFiles");

        File.WriteAllText($"{_dir}/Config.yaml", $"""
            batchSize: 2
            useContinuousWorkerPool: false
            glossaryPreset:
              usePresetChineseGlossary: false
            models:
              - name: Standard
                model: unused
                url: "http://localhost/unused"
            """);

        File.WriteAllText($"{_dir}/ManualTranslations.yaml", """
            - raw: 你好
              result: Hello
            - raw: 再见
              result: Goodbye
            """);

        var serializer = YamlHelper.CreateSerializer();
        TranslationLine Line(string text) => new() { Raw = text, Splits = [new TranslationSplit { Text = text }] };

        File.WriteAllText($"{_dir}/Raw/Export/A.txt", serializer.Serialize(new List<TranslationLine> { Line("你好"), Line("再见"), Line("你好"), Line("你好") }));
        File.WriteAllText($"{_dir}/Raw/Export/B.txt", serializer.Serialize(new List<TranslationLine> { Line("再见") }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private List<TranslationLine> ReadConverted(string path) =>
        YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(File.ReadAllText($"{_dir}/Converted/{path}.yaml"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schedulers_TranslateFromCache_AndPropagateDuplicates(bool pooled)
    {
        if (pooled)
            await TranslationService.TranslateViaLlmAsyncPooled(_dir, false, _files);
        else
            await TranslationService.TranslateViaLlmAsyncBatched(_dir, false, _files);

        Assert.Equal(["Hello", "Goodbye", "Hello", "Hello"], ReadConverted("A.txt").Select(l => l.Splits[0].Translated));
        Assert.Equal(["Goodbye"], ReadConverted("B.txt").Select(l => l.Splits[0].Translated));
    }

    [Fact]
    public async Task BruteForce_ReusesCorpusAcrossPasses_AndTranslatesEverything()
    {
        // Brute force starts with a rules pass over existing Converted files - seed them untranslated.
        Directory.CreateDirectory($"{_dir}/Converted");
        foreach (var file in _files)
            File.Copy($"{_dir}/Raw/Export/{file.Path}", $"{_dir}/Converted/{file.Path}.yaml");

        await TranslationWorkflow.TranslateLinesBruteForce(_dir, _files);

        Assert.Equal(["Hello", "Goodbye", "Hello", "Hello"], ReadConverted("A.txt").Select(l => l.Splits[0].Translated));
        Assert.Equal(["Goodbye"], ReadConverted("B.txt").Select(l => l.Splits[0].Translated));
        Assert.All(ReadConverted("A.txt"), l => Assert.False(l.Splits[0].FlaggedForRetranslation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schedulers_SecondRunWithNothingToDo_DoesNotRewriteFiles(bool pooled)
    {
        Task Run() => pooled
            ? TranslationService.TranslateViaLlmAsyncPooled(_dir, false, _files)
            : TranslationService.TranslateViaLlmAsyncBatched(_dir, false, _files);

        await Run();
        var stamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc($"{_dir}/Converted/A.txt.yaml", stamp);

        await Run();

        Assert.Equal(stamp, File.GetLastWriteTimeUtc($"{_dir}/Converted/A.txt.yaml"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schedulers_FailedRepresentative_DoesNotEraseDuplicateTranslation(bool pooled)
    {
        // Representative is empty (needs translation) and unresolvable without an LLM call that will
        // fail; its duplicate already carries a translation that must survive propagation.
        var serializer = YamlHelper.CreateSerializer();
        Directory.CreateDirectory($"{_dir}/Converted");
        File.WriteAllText($"{_dir}/Converted/A.txt.yaml", serializer.Serialize(new List<TranslationLine>
        {
            new() { Raw = "未知", Splits = [new TranslationSplit { Text = "未知" }] },
            new() { Raw = "未知", Splits = [new TranslationSplit { Text = "未知", Translated = "Unknown" }] },
        }));

        TextFileToSplit[] files = [new TextFileToSplit { Path = "A.txt" }];
        if (pooled)
            await TranslationService.TranslateViaLlmAsyncPooled(_dir, false, files);
        else
            await TranslationService.TranslateViaLlmAsyncBatched(_dir, false, files);

        Assert.Equal("Unknown", ReadConverted("A.txt")[1].Splits[0].Translated);
    }
}
