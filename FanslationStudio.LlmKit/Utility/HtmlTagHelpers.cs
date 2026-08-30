using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Utility;

public record TagValidationResult(bool IsValid, HashSet<string> MissingTags, HashSet<string> ExtraTags);

public static class HtmlTagHelpers
{
    public static TagValidationResult ValidateTags(string raw, string translated, bool allowMissingColors)
    {
        HashSet<string> rawTags = ExtractTagsWithAttributes(raw, true);
        HashSet<string> translatedTags = ExtractTagsWithAttributes(translated, false);

        var response = rawTags.SetEquals(translatedTags);
        var missingTags = new HashSet<string>();
        var extraTags = new HashSet<string>();

        if (!response
            && allowMissingColors
            && rawTags.Count != translatedTags.Count) //So if its got them get it right
        {
            rawTags.RemoveWhere(tag => tag.Contains("color"));
            translatedTags.RemoveWhere(tag => tag.Contains("color"));
            response = rawTags.SetEquals(translatedTags);
        }

        // Test for Trimmed tags (when we're using the raw in the validation test)
        if (!response)
        {
            var trimmedTags = new HashSet<string>();
            foreach (var tag in rawTags)
                trimmedTags.Add(tag.Trim());

            response = trimmedTags.SetEquals(translatedTags);

            if (!response)
            {
                // Calculate missing and extra tags
                missingTags = new HashSet<string>(trimmedTags.Except(translatedTags));
                extraTags = new HashSet<string>(translatedTags.Except(trimmedTags));
            }
        }
        else if (!response)
        {
            // Calculate missing and extra tags without trimming
            missingTags = new HashSet<string>(rawTags.Except(translatedTags));
            extraTags = new HashSet<string>(translatedTags.Except(rawTags));
        }

        return new TagValidationResult(response, missingTags, extraTags);
    }

    private static HashSet<string> ExtractTagsWithAttributes(string input, bool updateSizes)
    {
        var tags = new HashSet<string>();
        var regex = new Regex(@"<(/?\w+\s*[^>]*)>");
        foreach (Match match in regex.Matches(input))
        {
            var tag = match.Groups[1].Value;

            // Update size tags if needed
            if (updateSizes && tag.StartsWith("size"))
            {
                var newSize = StringTokenReplacer.CalculateNewSize($"<{tag}>");
                tag = tag.Contains("#") ? $"size=#{newSize}" : $"size={newSize}";
            }

            tags.Add(tag);
        }
        return tags;
    }

    public static List<string> ExtractTagsListWithAttributes(string input, params string[] ignore)
    {
        var tags = new List<string>();
        var regex = new Regex(@"<(\w+\s*[^/>]*)>");
        foreach (Match match in regex.Matches(input))
        {
            var tagValue = match.Groups[1].Value;
            if (!ignore.Any(i => tagValue.StartsWith(i)))
                tags.Add($"<{tagValue}>");
        }
        return tags;
    }

    // "(?:...)+$"/"^(?:...)+" alternate between two kinds of invisible-at-render-time token: real
    // HTML/rich-text tags, and this game's literal backslash escape sequences ("\n", "\r", "\t" -
    // two literal ASCII characters, backslash + letter, not an actual control character) used as a
    // newline/tab marker in raw game text. Both render with zero visible width of their own, so
    // neither should count as the "real" trailing/leading character for word-boundary spacing
    // purposes - confirmed necessary the hard way: without the escape-sequence half of this, the
    // trailing "n" of a literal "\n" was being treated as a real Latin letter, inserting a spurious
    // space between "\n" and the CJK/translated text that followed it.
    private static readonly Regex TrailingTagsRegex = new(@"(?:<\/?[A-Za-z][^<>]*>|\\[nrt])+$", RegexOptions.Compiled);
    private static readonly Regex LeadingTagsRegex = new(@"^(?:<\/?[A-Za-z][^<>]*>|\\[nrt])+", RegexOptions.Compiled);

    /// <summary>
    /// Returns the last *visible* character of <paramref name="s"/> - i.e. skipping any run of
    /// complete HTML/rich-text tags ("&lt;b&gt;", "&lt;/color&gt;", etc.) or literal "\n"/"\r"/"\t"
    /// escape sequences sitting at the very end - since neither renders as real text. Used by
    /// callers that need to decide whether a word-boundary space is needed right before something
    /// that will be placed immediately after <paramref name="s"/> (see
    /// <see cref="CompoundFieldSplitter.Reconstruct"/>).
    /// </summary>
    public static char? EffectiveTrailingChar(string s)
    {
        var stripped = TrailingTagsRegex.Replace(s, string.Empty);
        return stripped.Length > 0 ? stripped[^1] : null;
    }

    /// <summary>Leading-edge counterpart to <see cref="EffectiveTrailingChar"/>.</summary>
    public static char? EffectiveLeadingChar(string s)
    {
        var stripped = LeadingTagsRegex.Replace(s, string.Empty);
        return stripped.Length > 0 ? stripped[0] : null;
    }

    /// <summary>
    /// Splits off any trailing run of tags/escape-sequences (see <see cref="EffectiveTrailingChar"/>)
    /// from <paramref name="s"/>, returning the real text and the markup separately so a caller can
    /// splice something (e.g. a word-boundary space) in BETWEEN them instead of after - keeping an
    /// opening tag like "&lt;b&gt;" attached to the fragment it's about to wrap, rather than
    /// stranding it on the wrong side of an inserted space.
    /// </summary>
    public static (string Core, string TrailingMarkup) SplitTrailingMarkup(string s)
    {
        var match = TrailingTagsRegex.Match(s);
        return match.Success ? (s[..match.Index], s[match.Index..]) : (s, string.Empty);
    }

    /// <summary>Leading-edge counterpart to <see cref="SplitTrailingMarkup"/>.</summary>
    public static (string LeadingMarkup, string Core) SplitLeadingMarkup(string s)
    {
        var match = LeadingTagsRegex.Match(s);
        return match.Success ? (match.Value, s[match.Length..]) : (string.Empty, s);
    }

    public static string TrimHtmlTagsInContent(string input)
    {
        // Regular expression to match HTML tags and remove extra spaces, including self-closing tags
        var tagPattern = new Regex(@"<\s*(\w+)(.*?)\s*/?>");

        // Replace each tag by trimming unnecessary spaces inside the tag
        return tagPattern.Replace(input, match =>
        {
            var tagName = match.Groups[1].Value;
            var attributes = match.Groups[2].Value.Trim();

            // Determine if the tag is self-closing
            bool isSelfClosing = match.Value.EndsWith("/>");

            // Rebuild the tag with no extra spaces and ensure self-closing tag has the slash without spaces before it
            return isSelfClosing
                ? $"<{tagName}{(string.IsNullOrEmpty(attributes) ? "" : " " + attributes)}/>"
                : $"<{tagName}{(string.IsNullOrEmpty(attributes) ? "" : " " + attributes)}>";
        });
    }
}

