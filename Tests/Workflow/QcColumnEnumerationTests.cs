using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

/// <summary>
/// <see cref="QualityControlWorkflow.EnumerateColumns"/> - the single column iterator every QC pass
/// (review, rule check, resets, flagged report) now shares.
/// </summary>
public sealed class QcColumnEnumerationTests
{
    [Fact(DisplayName = "Groups a CSV line's fragments by Split, sorted by SubIndex, anchored on SubIndex 0 with its template")]
    public void GroupsCompoundCsvColumn()
    {
        var sub1 = new TranslationSplit { Split = 2, SubIndex = 1, Text = "乙", Translated = "B" };
        var sub0 = new TranslationSplit { Split = 2, SubIndex = 0, Text = "甲", Translated = "A" };
        var plain = new TranslationSplit { Split = 3, SubIndex = 0, Text = "丙", Translated = "C" };
        var line = new TranslationLine
        {
            Splits = [sub1, plain, sub0],
            Templates = [new FieldTemplate { Split = 2, Template = "{0}<br>{1}" }],
        };

        var columns = QualityControlWorkflow.EnumerateColumns(line).ToList();

        Assert.Equal(2, columns.Count);
        var compound = columns.Single(c => c.Key == "#2");
        Assert.Same(sub0, compound.Anchor);
        Assert.Equal([sub0, sub1], compound.Fragments);
        Assert.NotNull(compound.Template);
        Assert.Equal(CompoundFieldSplitter.Reconstruct("{0}<br>{1}", ["甲", "乙"]), compound.RawText);
        Assert.Equal(CompoundFieldSplitter.Reconstruct("{0}<br>{1}", ["A", "B"]), compound.ComputeEffectiveTranslated());

        var plainColumn = columns.Single(c => c.Key == "#3");
        Assert.Same(plain, plainColumn.Anchor);
        Assert.Null(plainColumn.Template);
        Assert.Equal("丙", plainColumn.RawText);
        Assert.Equal("C", plainColumn.ComputeEffectiveTranslated());
    }

    [Fact(DisplayName = "JSON fields sharing Split 0 stay separate columns keyed by SplitPath")]
    public void KeepsJsonFieldPathsSeparate()
    {
        var line = new TranslationLine
        {
            Splits =
            [
                new TranslationSplit { Split = 0, SplitPath = "Name", Text = "名" },
                new TranslationSplit { Split = 0, SplitPath = "Desc", Text = "述" },
            ],
            Templates = [new FieldTemplate { Split = 0, SplitPath = "Desc", Template = "{0}" }],
        };

        var columns = QualityControlWorkflow.EnumerateColumns(line).ToList();

        Assert.Equal(["Name", "Desc"], columns.Select(c => c.Key));
        Assert.Null(columns[0].Template);
        Assert.NotNull(columns[1].Template);
    }

    [Fact(DisplayName = "A column with no SubIndex 0 fragment anchors on its lowest SubIndex")]
    public void FallsBackToLowestSubIndexAnchor()
    {
        var sub2 = new TranslationSplit { Split = 1, SubIndex = 2, Text = "乙" };
        var sub1 = new TranslationSplit { Split = 1, SubIndex = 1, Text = "甲" };
        var line = new TranslationLine { Splits = [sub2, sub1] };

        var column = Assert.Single(QualityControlWorkflow.EnumerateColumns(line));

        Assert.Same(sub1, column.Anchor);
    }
}
