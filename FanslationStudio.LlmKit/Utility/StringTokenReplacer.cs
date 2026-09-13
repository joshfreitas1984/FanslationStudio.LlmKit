using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core.Tokens;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Replace things we know cause issues with the LLM with straight tokens which it seems to handle ok. 
/// </summary>
public class StringTokenReplacer
{
    private static readonly Regex PlaceholderRegex = new(@"(\{[^{}]+\})", RegexOptions.Compiled);
    private static readonly Regex CoordinateRegex = new(@"\(-?\d+,-?\d+\)", RegexOptions.Compiled);
    private static readonly Regex NumericValueRegex = new(@"(?<![{<]|color=|<[^>]*)(?:[+-]?(?:\d+\.\d*|\.\d+|\d+))(?![}>])", RegexOptions.Compiled);
    private static readonly Regex ColorStartRegex = new(@"<color=[^>]+>", RegexOptions.Compiled);
    private static readonly Regex KeyPressRegex = new(@"<\w+\s+>", RegexOptions.Compiled);
    public static readonly Regex SizeRegex = new(@"<size=[^>]+>", RegexOptions.Compiled);
    public static readonly Regex SizeValueRegex = new(@"(?<=<size=)\d+", RegexOptions.Compiled);
    public static readonly Regex SizeValue2Regex = new(@"(?<=<size=#)\d+", RegexOptions.Compiled);
    public static readonly Regex SizeValueCurlyRegex = new Regex(@"\{size=(\d+)\}", RegexOptions.Compiled);

    private Dictionary<int, string> placeholderMap = new();
    private Dictionary<string, string> colorMap = new();
    private Dictionary<string, string> sizeMap = new();

    public static Regex? ExtraTokenRegex;

    public static void SetExtraTokens(List<string> extraTokens)
    {
        // No extra tokens configured - leave the regex unset rather than building one from an
        // empty pattern (string.Join on an empty sequence yields "", and a regex with an empty
        // pattern matches the empty string at every position, which would corrupt every line).
        if (extraTokens == null || extraTokens.Count == 0)
        {
            ExtraTokenRegex = null;
            return;
        }

        var extraTokenPattern = string.Join("|", extraTokens.Select(Regex.Escape));
        ExtraTokenRegex = new Regex(extraTokenPattern, RegexOptions.Compiled);
    }

    public static int CalculateNewSize(string sizeTag)
    {
        if (Int32.TryParse(sizeTag, out int directSize))
            return (int)Math.Round(directSize * 0.7);

        var sizeString = SizeValueRegex.Match(sizeTag).Value;
        if (string.IsNullOrEmpty(sizeString))
            sizeString = SizeValue2Regex.Match(sizeTag).Value;

        return (int)Math.Round(Convert.ToInt32(sizeString) * 0.7);
    }

    /// <summary>
    /// Shrinks the numeric value inside every <c>&lt;size=N&gt;</c>/<c>{size=N}</c> tag in
    /// <paramref name="input"/> via <see cref="CalculateNewSize"/>, as a plain deterministic string
    /// transform - no tokenization/placeholder mapping, since this runs once (in
    /// <see cref="CompoundFieldSplitter.Decompose"/>, before any LLM ever sees the text) rather than
    /// per-LLM-call like <see cref="Replace"/>.
    /// </summary>
    public static string ShrinkSizeTagsOnly(string input)
    {
        var result = new StringBuilder(input);

        result.Replace(SizeValueCurlyRegex, match =>
        {
            var sizeValue = CalculateNewSize(match.Groups[1].Value);
            return $"{{size={sizeValue}}}";
        });

        result.Replace(SizeRegex, match =>
        {
            var sizeTag = match.Value;
            var sizeValue = CalculateNewSize(sizeTag);
            var hasHash = sizeTag.Contains("#");
            return hasHash ? $"<size=#{sizeValue}>" : $"<size={sizeValue}>";
        });

        return result.ToString();
    }

    public string Replace(string input)
    {
        int index = 0;
        int colorIndex = 0;
        int sizeIndex = 0;
        placeholderMap.Clear();
        colorMap.Clear();
        sizeMap.Clear();
        var result = new StringBuilder(input);

        // Handle {size=24} style tags. Masking only, no arithmetic here - the value has already
        // been shrunk once, up front, by CompoundFieldSplitter.Decompose (via ShrinkSizeTagsOnly)
        // before this text was ever split into fragments/templates, so re-shrinking here would
        // double-apply CalculateNewSize's 0.7 multiplier.
        result.Replace(SizeValueCurlyRegex, match =>
        {
            var sizeString = match.Groups[1].Value;
            var key = $"{{size={sizeIndex++}}}";
            var replacement = $"{{size={sizeString}}}";
            sizeMap.Add(key, replacement);
            return key;
        });

        result.Replace(PlaceholderRegex, match =>
        {
            placeholderMap.Add(index, match.Value);
            return $"{{{index++}}}";
        });

        result.Replace(CoordinateRegex, match =>
        {
            placeholderMap.Add(index, match.Value);
            return $"{{{index++}}}";
        });

        result.Replace(ColorStartRegex, match =>
        {
            string replacement = $"<color={colorIndex++}>";
            colorMap.Add(replacement, match.Value);
            return replacement;
        });

        result.Replace(KeyPressRegex, match =>
        {
            placeholderMap.Add(index, match.Value.Replace(" ", ""));
            return $"{{{index++}}}";
        });

        // Mask size tags from the LLM. No arithmetic here - see the {size=N} block above for why:
        // the value was already shrunk once by CompoundFieldSplitter.Decompose.
        result.Replace(SizeRegex, match =>
        {
            var sizeTag = match.Value;
            var hasHash = sizeTag.Contains("#");
            var key = hasHash ? $"<size=#{sizeIndex++}>" : $"<size={sizeIndex++}>";

            sizeMap.Add(key, sizeTag);
            return key;
        });

        result.Replace(NumericValueRegex, match =>
        {
            placeholderMap.Add(index, match.Value);
            return $"{{{index++}}}";
        });

        // Bug fix: this used to be gated on an instance field that was declared but never
        // populated anywhere, so it was always empty and this block never ran - meaning tokens
        // like `#PlayerName#` were never swapped out for a `{n}` placeholder before being sent to
        // the LLM, and the raw `#...#` text (which the LLM can mangle or drop) went straight into
        // the prompt instead. Gate on the static regex actually being configured (see
        // SetExtraTokens, called once from ConfigurationExtensions.GetConfiguration with
        // LlmConfig.ExtraStringTokenReplacers).
        if (ExtraTokenRegex != null)
        {
            result.Replace(ExtraTokenRegex, match =>
            {
                placeholderMap.Add(index, match.Value);
                return $"{{{index++}}}";
            });
        }

        return result.ToString();
    }

    public string Restore(string input)
    {
        var result = new StringBuilder(input);

        result.Replace(PlaceholderRegex, match =>
        {
            if (int.TryParse(match.Value.Trim('{', '}'), out int index)
                && placeholderMap.TryGetValue(index, out string? original))
            {
                return original;
            }
            return match.Value;
        });

        foreach (var size in sizeMap)
            result.Replace(size.Key, size.Value);

        foreach (var color in colorMap)
            result.Replace(color.Key, color.Value);

        return result.ToString();
    }

    public static string CleanTranslatedForApplyRules(string input)
    {
        return input;
        //return EmojiRegex.Replace(input, "");
    }
}