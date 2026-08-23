using FanslationStudio.LlmKit.Utility;

namespace Tests;

/// <summary>
/// Covers <see cref="StringTokenReplacer.SetExtraTokens"/> / <see cref="StringTokenReplacer.Replace"/>
/// handling of <c>ExtraStringTokenReplacers</c>-style literal tokens (e.g. game placeholder tokens
/// like "#PlayerName#"). Regression coverage for a bug where the extra-token substitution branch in
/// <see cref="StringTokenReplacer.Replace"/> was gated on a private instance field that was never
/// populated, so it never ran regardless of configuration - meaning literal tokens were sent to the
/// LLM as-is instead of being swapped out for a "{n}" placeholder first.
/// </summary>
public class StringTokenReplacerTests
{
    public StringTokenReplacerTests()
    {
        // Reset to a known state before each test - StringTokenReplacer.ExtraTokenRegex is static,
        // shared across the whole test run/process.
        StringTokenReplacer.SetExtraTokens(new List<string>());
    }

    [Fact(DisplayName = "Extra token is replaced with a placeholder and restored intact")]
    public void ExtraToken_IsReplacedAndRestored()
    {
        StringTokenReplacer.SetExtraTokens(new List<string> { "#PlayerName#" });

        var replacer = new StringTokenReplacer();
        var original = "你好，#PlayerName#，欢迎回来";

        var replaced = replacer.Replace(original);
        var restored = replacer.Restore(replaced);

        Assert.DoesNotContain("#PlayerName#", replaced);
        Assert.Equal(original, restored);
    }

    [Fact(DisplayName = "No extra tokens configured leaves input untouched by the extra-token branch")]
    public void NoExtraTokensConfigured_DoesNotAlterUnrelatedText()
    {
        StringTokenReplacer.SetExtraTokens(new List<string>());

        var replacer = new StringTokenReplacer();
        var original = "你好，世界";

        var replaced = replacer.Replace(original);
        var restored = replacer.Restore(replaced);

        Assert.Equal(original, replaced);
        Assert.Equal(original, restored);
    }

    [Fact(DisplayName = "Multiple configured extra tokens are each replaced and restored")]
    public void MultipleExtraTokens_AreEachReplacedAndRestored()
    {
        StringTokenReplacer.SetExtraTokens(new List<string> { "#PlayerName#", "#GuildName#" });

        var replacer = new StringTokenReplacer();
        var original = "#PlayerName#加入了#GuildName#";

        var replaced = replacer.Replace(original);
        var restored = replacer.Restore(replaced);

        Assert.DoesNotContain("#PlayerName#", replaced);
        Assert.DoesNotContain("#GuildName#", replaced);
        Assert.Equal(original, restored);
    }
}
