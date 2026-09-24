using System.Reflection;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public class TranslationWorkflowTests
{
    // FindQcAnchor moved to QualityReviewHelpers (see Tests/Utility/QualityReviewHelpersTests.cs)
    // so TranslationService's own retranslation paths could reuse it too - its resolution-order
    // regression tests now live there alongside the rest of QualityReviewHelpers' coverage.

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
