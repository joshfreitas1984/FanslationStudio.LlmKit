using System.Reflection;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public class TranslationWorkflowTests
{
    private static TranslationSplit InvokeFindQcAnchor(TranslationLine line, TranslationSplit split)
    {
        var method = typeof(TranslationWorkflow).GetMethod(
            "FindQcAnchor", BindingFlags.NonPublic | BindingFlags.Static)!;

        return (TranslationSplit)method.Invoke(null, [line, split])!;
    }

    // Regression test: retranslating a non-zero SubIndex fragment of a compound/templated column
    // used to call TranslationSplit.ResetQcState() on the retranslated fragment itself, which never
    // carries QC state for a compound column - only the SubIndex == 0 fragment does (see the anchor
    // convention in docs/features/translation-pipeline/quality-review-pass.md). That left the anchor's
    // stale QcStatus/QcQualityScore/QcTranslated sitting untouched until IsQcReviewFresh's own dynamic
    // recompute caught up on the next QC run - this test locks in that TranslationWorkflow.UpdateSplit
    // now resets the correct fragment (the anchor) immediately instead.
    [Fact(DisplayName = "FindQcAnchor resolves the SubIndex == 0 fragment for a CSV-style compound column")]
    public void FindQcAnchorResolvesAnchorForCsvStyleColumn()
    {
        var anchorSplit = new TranslationSplit { Split = 1, SubIndex = 0, Text = "part0", Translated = "Part 0" };
        var subIndex1 = new TranslationSplit { Split = 1, SubIndex = 1, Text = "part1", Translated = "Part 1" };
        var subIndex2 = new TranslationSplit { Split = 1, SubIndex = 2, Text = "part2", Translated = "Part 2" };
        var otherColumn = new TranslationSplit { Split = 2, SubIndex = 0, Text = "unrelated", Translated = "Unrelated" };

        var line = new TranslationLine
        {
            Splits = [anchorSplit, subIndex1, subIndex2, otherColumn],
        };

        // Retranslating subIndex2 (not the anchor) must still resolve back to subIndex 0's fragment,
        // not to subIndex2 itself and not to the unrelated column sharing the same line.
        var resolved = InvokeFindQcAnchor(line, subIndex2);

        Assert.Same(anchorSplit, resolved);
    }

    [Fact(DisplayName = "FindQcAnchor resolves the SubIndex == 0 fragment for a JSON-style field-path column")]
    public void FindQcAnchorResolvesAnchorForJsonStyleColumn()
    {
        // JSON field-path files leave Split == 0 for every field on the line (see
        // QualityReviewWorkflow.ColumnKey's own doc comment) and disambiguate columns via SplitPath
        // instead - this must be respected here too, or a JSON compound field's retranslated
        // non-zero-SubIndex fragment would get grouped with an unrelated field that also has
        // Split == 0.
        var anchorSplit = new TranslationSplit { Split = 0, SplitPath = "Desc", SubIndex = 0, Text = "part0", Translated = "Part 0" };
        var subIndex1 = new TranslationSplit { Split = 0, SplitPath = "Desc", SubIndex = 1, Text = "part1", Translated = "Part 1" };
        var unrelatedField = new TranslationSplit { Split = 0, SplitPath = "Name", SubIndex = 0, Text = "name", Translated = "Name" };

        var line = new TranslationLine
        {
            Splits = [anchorSplit, subIndex1, unrelatedField],
        };

        var resolved = InvokeFindQcAnchor(line, subIndex1);

        Assert.Same(anchorSplit, resolved);
    }

    [Fact(DisplayName = "FindQcAnchor returns the split itself for a plain, single-fragment column")]
    public void FindQcAnchorReturnsSelfForPlainColumn()
    {
        var plainSplit = new TranslationSplit { Split = 3, SubIndex = 0, Text = "plain", Translated = "Plain" };
        var line = new TranslationLine { Splits = [plainSplit] };

        var resolved = InvokeFindQcAnchor(line, plainSplit);

        Assert.Same(plainSplit, resolved);
    }

    // Regression test: a QC correction was observed dropping the leading "-" off a stat/buff
    // tooltip's negative percentage ("-0.5%全属性" -> "0.5% All Attributes" instead of "‑0.5% All
    // Attributes") while the rest of the correction was otherwise fine - see the investigation this
    // test was added from. EvaluateRules' validation gate had nothing that checked a negative
    // number's sign survived, so the broken correction was accepted outright.
    [Theory(DisplayName = "IsMissingRequiredNegativeSign catches a dropped negative sign, not a preserved one")]
    [InlineData("-0.5%全属性\\n-0.5%内力上限", "0.5% All Attributes\\n‑0.5% Max Inner Power", true)] // one of two dropped
    [InlineData("-0.5%全属性\\n-0.5%内力上限", "‑0.5% All Attributes\\n‑0.5% Max Inner Power", false)] // both preserved (non-breaking hyphen)
    [InlineData("-0.5%全属性", "-0.5% All Attributes", false)] // preserved as plain ASCII hyphen too
    [InlineData("全属性", "All Attributes", false)] // no negative number in source at all
    public void IsMissingRequiredNegativeSignDetectsDroppedSign(string preparedRaw, string translated, bool expected)
    {
        var method = typeof(TranslationWorkflow).GetMethod(
            "IsMissingRequiredNegativeSign", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (bool)method.Invoke(null, [preparedRaw, translated])!;

        Assert.Equal(expected, result);
    }
}
