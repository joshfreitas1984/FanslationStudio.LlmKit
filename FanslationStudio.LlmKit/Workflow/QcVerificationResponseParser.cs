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

        if (!QcDefectCategoryTokens.TryParseList(unresolvedMatch.Groups[1].Value, out var unresolved))
            return new QcVerificationResult(false, [], [], 0);

        if (!QcDefectCategoryTokens.TryParseList(newDefectsMatch.Groups[1].Value, out var newDefects))
            return new QcVerificationResult(false, [], [], 0);

        // UNRESOLVED must be a subset of what was actually confirmed - a verifier naming a category
        // that was never part of CONFIRMED DEFECTS is a protocol violation, not a real signal. With
        // an EVIDENCE line (BaseQualityControlVerificationEvidencePrompt) the claim carries a quote
        // FilterByEvidence checks, so it is kept as a new defect instead of discarding a clear rejection.
        var unconfirmed = unresolved.Where(category => !confirmedDefects.Contains(category)).ToList();
        if (unconfirmed.Count > 0)
        {
            if (!EvidenceLineRegex.IsMatch(response))
                return new QcVerificationResult(false, [], [], 0);

            unresolved = unresolved.Except(unconfirmed).ToList();
            newDefects = newDefects.Concat(unconfirmed).Distinct().ToList();
        }

        // \d+ can still overflow int - a run of digits that long is far past the 0-100 scale anyway.
        var score = int.TryParse(scoreMatch.Groups[1].Value, out var parsedScore) ? Math.Clamp(parsedScore, 0, 100) : 100;
        return new QcVerificationResult(true, unresolved, newDefects, score, ParseEvidence(response));
    }

    private static readonly Regex EvidenceLineRegex = new(
        @"^\s*EVIDENCE:\s*(.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex EvidenceEntryRegex = new(
        @"^\s*([A-Za-z_]+)\s*:\s*[""“”']?(.*?)[""“”']?\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The optional <c>EVIDENCE:</c> line of BaseQualityControlVerificationEvidencePrompt
    /// (<c>CATEGORY: "quote" | CATEGORY: "quote"</c>). Null when the line is absent; an entry that
    /// does not parse is skipped, never a parse failure of the whole verdict.
    /// </summary>
    private static Dictionary<QcDefectCategory, string>? ParseEvidence(string response)
    {
        var line = EvidenceLineRegex.Match(response);
        if (!line.Success)
            return null;

        var evidence = new Dictionary<QcDefectCategory, string>();
        foreach (var entry in line.Groups[1].Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = EvidenceEntryRegex.Match(entry);
            if (!match.Success)
                continue;

            var category = QcDefectCategoryTokens.Parse(match.Groups[1].Value);
            var quote = match.Groups[2].Value.Trim();
            if (category is not (QcDefectCategory.Unknown or QcDefectCategory.None) && quote.Length > 0)
                evidence.TryAdd(category, quote);
        }

        return evidence;
    }

    /// <summary>
    /// Drops every unresolved/new claim whose quoted evidence is not real: a DROPPED_CONTENT quote must
    /// come from <paramref name="source"/> (text found only in the old translation is not dropped
    /// content), any other quote from <paramref name="source"/> or <paramref name="candidate"/>. A claim
    /// with no evidence entry at all is kept - only a quote that provably points nowhere (or is only
    /// punctuation) is overruled. A verdict still rejecting afterwards scores 0, exactly as the plain
    /// prompt's rejections do, so its "real grade" can never let a disputed candidate clear
    /// minAcceptableScore; the grade is only used when every claim was overruled. Unchanged when the
    /// verdict carries no EVIDENCE line.
    /// </summary>
    public static QcVerificationResult FilterByEvidence(QcVerificationResult result, string source, string candidate)
    {
        if (!result.Success || result.Evidence == null)
            return result;

        bool Supported(QcDefectCategory category)
        {
            if (!result.Evidence.TryGetValue(category, out var quote))
                return true;

            if (!quote.Any(char.IsLetterOrDigit))
                return false;

            var inSource = source.Contains(quote, StringComparison.OrdinalIgnoreCase);
            return category == QcDefectCategory.DroppedContent
                ? inSource
                : inSource || candidate.Contains(quote, StringComparison.OrdinalIgnoreCase);
        }

        var filtered = result with
        {
            UnresolvedDefects = result.UnresolvedDefects.Where(Supported).ToList(),
            NewDefects = result.NewDefects.Where(Supported).ToList(),
        };
        return filtered.Accepted ? filtered : filtered with { Score = 0 };
    }
}
