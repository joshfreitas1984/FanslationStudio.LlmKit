using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Assessments;

public class GlossaryScansTests
{
    private static readonly List<GlossaryLine> Glossary =
    [
        new("三七", "Sanqi"),
        new("三七开", "70/30 split"),
        new("掌门", "Sect Leader") { AllowedAlternatives = ["Sect Master"] },
    ];

    private static readonly List<CorpusLine> Lines =
    [
        new("f", "掌门来了", "The Sect Leader came"),
        new("f", "掌门走了", "The Sect Master left"),
        new("f", "掌门死了", "The boss died"),
        new("f", "三七开分账", "Split 70/30"),
        new("f", "吃三七", "Eat Sanqi"),
    ];

    [Fact(DisplayName = "Glossary scans - counts matches, hits via result or alternative, and shadowed injections")]
    public void EntryStatsCountMatchesHitsAndShadowing()
    {
        var stats = GlossaryScans.EntryStats(Glossary, Lines).ToDictionary(s => s.Raw);

        Assert.Equal((3, 2, 1), (stats["掌门"].Matched, stats["掌门"].Hit, stats["掌门"].Miss));
        Assert.Equal(1, stats["三七"].Shadowed); // only inside 三七开 in line 4
        Assert.Equal((1, 1), (stats["三七"].Matched, stats["三七"].Hit));
        Assert.Equal((1, 0), (stats["三七开"].Matched, stats["三七开"].Hit));
        Assert.Equal(1 / 3.0, stats["掌门"].MissRate, 6);
    }

    [Fact(DisplayName = "Glossary scans - change impact counts source matches still using the old result")]
    public void ChangeImpactCountsOldResult()
    {
        Assert.Equal((3, 2), GlossaryScans.ChangeImpact(Lines, "掌门", "Sect"));
    }

    [Fact(DisplayName = "Prompt-leak detector - flags echoed correction-prompt text and nothing else")]
    public void PromptLeakDetectorFlagsEchoedPrompt()
    {
        List<CorpusLine> lines =
        [
            new("f", "那小姑娘哭了", "While correcting, also verify: The girl cried"),
            new("f", "那小姑娘哭了", "The girl cried"),
            new("f", "他纠正了姿势", "He fixed his stance while correcting the form"),
            new("f", "好", "Gender‑neutral language only"),
        ];

        var flagged = GlossaryScans.Flagged(lines, l => LineValidation.FindPromptLeak(l.Source, l.Translated) != null);

        Assert.Equal([0, 3], flagged.Select(l => lines.IndexOf(l)).ToArray());
    }

    [Fact(DisplayName = "Glossary scans - detector blast radius returns the flagged lines")]
    public void FlaggedAppliesDetector()
    {
        Assert.Equal(2, GlossaryScans.Flagged(Lines, l => l.Translated.Contains("Sect")).Count);
    }
}
