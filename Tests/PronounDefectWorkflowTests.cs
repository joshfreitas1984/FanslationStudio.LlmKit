using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace FanslationStudio.LlmKit.Tests;

public class PronounDefectWorkflowTests
{
    private static TranslationLine Line(string source, string translated, bool flagged = false) => new()
    {
        Raw = source,
        Splits = [new TranslationSplit { Text = source, Translated = translated, FlaggedForRetranslation = flagged }],
    };

    private static string CreateCorpus(out TextFileToSplit textFile, out List<TranslationLine> lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "llmkit-pronoun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Converted"));

        lines =
        [
            Line("（笑着把银两收起来）", "(Smiling, he put the silver away)"),                 // 0 invented gender, no context
            Line("（他笑着把银两收起来）", "(Smiling, he put the silver away)"),               // 1 source states gender: fine
            Line("只见阮芷躺在一块草席上，正不住咳嗽。", "I see Ruan Zhi lying on a mat, coughing."), // 2 narrated as I
            Line("（揉了揉被震麻的手腕）", "(Rubbing her numbed wrist)"),                      // 3 known male speaker, "her"
            Line("（点了点头）", "(Smiled, she nodded)"),                                   // 4 known female speaker, "she": correct
            Line("（笑着把银两收起来）", "(Smiling, he put the silver away)", flagged: true),   // 5 already flagged: skipped
        ];

        textFile = new TextFileToSplit { Path = "Test.txt", TextFileType = TextFileType.RawCsv };
        File.WriteAllText(Path.Combine(dir, "Converted", "Test.txt.yaml"), YamlHelper.CreateSerializer().Serialize(lines));
        return dir;
    }

    private static GameHooks HooksFor() => new()
    {
        LineContextProvider = (_, _, lines) => new Dictionary<TranslationSplit, LineContext>
        {
            [lines[3].Splits[0]] = new LineContext("male", true, LineContext.Male),
            [lines[4].Splits[0]] = new LineContext("female", true, LineContext.Female),
        },
    };

    [Fact(DisplayName = "PronounDefectWorkflow dry run reports each category and leaves Converted untouched")]
    public async Task DryRun_ReportsWithoutWriting()
    {
        var dir = CreateCorpus(out var textFile, out _);
        try
        {
            var path = Path.Combine(dir, "Converted", "Test.txt.yaml");
            var before = File.ReadAllText(path);

            var result = await PronounDefectWorkflow.RunAsync(dir, [textFile], flagForRetranslation: false, HooksFor());

            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(3, result.Hits.Count);
            Assert.Contains(result.Hits, h => h.Category == "InventedGender" && h.Source == "（笑着把银两收起来）");
            Assert.Contains(result.Hits, h => h.Category == "NarratedAsFirstPerson");
            Assert.Contains(result.Hits, h => h.Category == "WrongGender" && h.Source == "（揉了揉被震麻的手腕）");
            // The female speaker's "she" is right, so it is counted as correct and not reported.
            Assert.Equal(1, result.GenderKnownAndCorrect);
            Assert.True(File.Exists(Path.Combine(dir, "TestResults", "PronounRetranslation.yaml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact(DisplayName = "PronounDefectWorkflow flag run sets FlaggedForRetranslation on exactly the hits")]
    public async Task FlagRun_FlagsOnlyTheHits()
    {
        var dir = CreateCorpus(out var textFile, out _);
        try
        {
            await PronounDefectWorkflow.RunAsync(dir, [textFile], flagForRetranslation: true, HooksFor());

            var lines = YamlHelper.CreateDeserializer()
                .Deserialize<List<TranslationLine>>(File.ReadAllText(Path.Combine(dir, "Converted", "Test.txt.yaml")));

            Assert.Equal([true, false, true, true, false, true], lines.Select(l => l.Splits[0].FlaggedForRetranslation));
            Assert.Equal("InventedGender", lines[0].Splits[0].FlaggedMistranslation);
            Assert.Equal("WrongGender", lines[3].Splits[0].FlaggedMistranslation);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact(DisplayName = "PronounDefectWorkflow without a provider finds only invented gender and first-person narration")]
    public async Task NoProvider_FindsContextFreeDefects()
    {
        var dir = CreateCorpus(out var textFile, out _);
        try
        {
            var result = await PronounDefectWorkflow.RunAsync(dir, [textFile], flagForRetranslation: false);

            Assert.DoesNotContain(result.Hits, h => h.Category == "WrongGender");
            Assert.Equal(0, result.GenderKnownAndCorrect);
            Assert.Contains(result.Hits, h => h.Category == "InventedGender");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
