using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Utility;

public static partial class ColorTagHelpers
{
    public static bool StartsWithHalfColorTag(string input, out string start, out string end)
    {
        start = string.Empty;
        end = string.Empty;

        // Perform the match
        var isMatch = HalfColorTagFullMatchRegex().IsMatch(input);

        if (isMatch)
        {
            var match = HalfColorTagGroupRegex().Match(input);
            // If regex matches, set start and end
            start = match.Groups[1].Value; // Full <color> tag
            end = match.Groups[2].Value;   // Content after the <color> tag
        }

        return isMatch;
    }

    [GeneratedRegex(@"^<color=[^>]+>(?!.*<\/color>)(.*)$")]
    private static partial Regex HalfColorTagFullMatchRegex();

    [GeneratedRegex(@"(<color=[^>]+>)(?!.*<\/color>)(.*)")]
    private static partial Regex HalfColorTagGroupRegex();
}
