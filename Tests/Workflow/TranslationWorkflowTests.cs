using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using FanslationStudio.LlmKit;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public class TranslationWorkflowTests
{
    // FindQcAnchor moved to QualityControlHelpers (see Tests/Utility/QualityControlHelpersTests.cs)
    // so TranslationService's own retranslation paths could reuse it too - its resolution-order
    // regression tests now live there alongside the rest of QualityControlHelpers' coverage.

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
    [InlineData("气-10，嘴力提升100%", "Qi decreases by 10, verbal prowess improves by 100%", false)] // sign carried by wording
    [InlineData("防御-50%，攻击-20%", "Defense decreases by 50%, attack 20%", true)] // one sign still unaccounted for
    public void IsMissingRequiredNegativeSignDetectsDroppedSign(string preparedRaw, string translated, bool expected)
    {
        var method = typeof(TranslationWorkflow).GetMethod(
            "IsMissingRequiredNegativeSign", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (bool)method.Invoke(null, [preparedRaw, translated])!;

        Assert.Equal(expected, result);
    }

    [Theory(DisplayName = "CustomUnsafeToTranslateRule marks matching splits unsafe and leaves others alone")]
    [InlineData("Mortal.Combat.CombatEnemyController/<SetData>d__24,MoveNext,253,圖片 ,[]", false)]
    [InlineData("Mortal.Combat.CombatStatController/<ModifyStamina>d__266,MoveNext,97,圖片 ,[]", true)]
    public void CustomUnsafeToTranslateRuleMarksSplitUnsafe(string raw, bool expectedSafe)
    {
        var split = new TranslationSplit { Split = 3, Text = "圖片 ", Translated = "Image" };
        var line = new TranslationLine { Raw = raw, Splits = [split] };
        var textFile = new TextFileToSplit { Path = "dynamicStrings.txt", TextFileType = TextFileType.DynamicStrings };
        var config = new LlmConfig
        {
            Hooks = new GameHooks
            {
                CustomUnsafeToTranslateRule = (_, l, _) => l.Raw.StartsWith("Mortal.Combat.CombatEnemyController/<SetData>"),
            },
        };
        // The non-matching case runs the rest of the rules pass, which needs a model entry.
        config.Runtime.Models["Default"] = new ModelExecutionConfig();

        TranslationWorkflow.UpdateSplit(new ConcurrentBag<string>(), line, split, textFile, config,
            new Regex(LineValidation.ChineseCharPattern), new StringTokenReplacer());

        Assert.Equal(expectedSafe, split.SafeToTranslate);
    }
}
