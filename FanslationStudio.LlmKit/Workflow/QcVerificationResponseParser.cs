using FanslationStudio.LlmKit.Support;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Parses call 4's response - three labeled lines (UNRESOLVED/NEW_DEFECTS/SCORE) reporting whether a
/// proposed correction resolves every confirmed defect and whether it introduces a new one. See
/// <see cref="QcVerificationResult"/> for how the result is used.
/// </summary>
public static class QcVerificationResponseParser
{
    private static readonly Regex UnresolvedLineRegex = new(
        @"^\s*UNRESOLVED:\s*(.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex NewDefectsLineRegex = new(
        @"^\s*NEW_DEFECTS:\s*(.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ScoreLineRegex = new(
        @"^\s*SCORE:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public static QcVerificationResult Parse(string response, IReadOnlyList<QcDefectCategory> confirmedDefects)
    {
        var unresolvedMatch = UnresolvedLineRegex.Match(response);
        var newDefectsMatch = NewDefectsLineRegex.Match(response);
        var scoreMatch = ScoreLineRegex.Match(response);
        if (!unresolvedMatch.Success || !newDefectsMatch.Success || !scoreMatch.Success)
            return new QcVerificationResult(false, [], [], 0);

        if (!TryParseCategoryList(unresolvedMatch.Groups[1].Value, out var unresolved))
            return new QcVerificationResult(false, [], [], 0);

        // UNRESOLVED must be a subset of what was actually confirmed - a verifier naming a category
        // that was never part of CONFIRMED DEFECTS is a protocol violation, not a real signal.
        if (unresolved.Any(category => !confirmedDefects.Contains(category)))
            return new QcVerificationResult(false, [], [], 0);

        if (!TryParseCategoryList(newDefectsMatch.Groups[1].Value, out var newDefects))
            return new QcVerificationResult(false, [], [], 0);

        var score = Math.Clamp(int.Parse(scoreMatch.Groups[1].Value), 0, 100);
        return new QcVerificationResult(true, unresolved, newDefects, score);
    }

    private static bool TryParseCategoryList(string value, out List<QcDefectCategory> categories)
    {
        categories = [];
        var trimmed = value.Trim();
        if (trimmed.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var token in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                return false;

            var category = QcDefectCategoryTokens.Parse(token);
            if (category is QcDefectCategory.Unknown or QcDefectCategory.None)
                return false;

            if (categories.Contains(category))
                return false;

            categories.Add(category);
        }

        return categories.Count > 0;
    }
}
