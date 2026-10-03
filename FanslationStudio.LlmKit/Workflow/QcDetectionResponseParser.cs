using FanslationStudio.LlmKit.Support;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Workflow;

public static class QcDetectionResponseParser
{
    private static readonly Regex DefectsLineRegex = new(
        @"^\s*DEFECTS:\s*(.*?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public static QcDetectionResult Parse(string response)
    {
        var match = DefectsLineRegex.Match(response);
        if (!match.Success)
            return new QcDetectionResult(false, []);

        var value = match.Groups[1].Value.Trim();
        if (value.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return new QcDetectionResult(true, []);

        if (!QcDefectCategoryTokens.TryParseList(value, out var categories))
            return new QcDetectionResult(false, []);

        var findings = categories.Select(category => new QcDefectFinding(category)).ToList();

        // UNCERTAIN is a genuine "something's off but not confident enough to name it" signal -
        // never collapse it into an empty/clean result the way NONE is, and never let it stand
        // alongside a named category (the detector should never emit both at once; treat that
        // combination as a protocol violation, same as mixing NONE with a named category).
        if (findings.Count > 1 && findings.Any(finding => finding.Category == QcDefectCategory.Uncertain))
            return new QcDetectionResult(false, []);

        return new QcDetectionResult(true, findings);
    }
}