namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// Single source of truth for the upper-snake-case DEFECT token vocabulary (e.g. "GARBLED_NUMBER")
/// every QC prompt/parser shares - detection (calls 1/2), correction generation (call 3),
/// verification (call 4), and repair (call 5) all name defects using these same tokens, so there is
/// exactly one place that maps between them and <see cref="QcDefectCategory"/>.
/// </summary>
public static class QcDefectCategoryTokens
{
    public static QcDefectCategory Parse(string token) => token.Trim().ToUpperInvariant() switch
    {
        "NONE" => QcDefectCategory.None,
        "GARBLED_NUMBER" => QcDefectCategory.GarbledNumber,
        "DOMAIN_TERM" => QcDefectCategory.DomainTerm,
        "LOST_IDIOM" => QcDefectCategory.LostIdiom,
        "UNTRANSLATED_PINYIN" => QcDefectCategory.UntranslatedPinyin,
        "DROPPED_CONTENT" => QcDefectCategory.DroppedContent,
        "DROPPED_STUTTER" => QcDefectCategory.DroppedStutter,
        "HARD_TO_PARSE_SEAM" => QcDefectCategory.HardToParseSeam,
        "OTHER_NAMED_DEFECT" => QcDefectCategory.OtherNamedDefect,
        "MEANING_REVERSAL" => QcDefectCategory.MeaningReversal,
        "UNCERTAIN" => QcDefectCategory.Uncertain,
        "UNNATURAL_PHRASING" => QcDefectCategory.UnnaturalPhrasing,
        _ => QcDefectCategory.Unknown,
    };

    /// <summary>Throws for <see cref="QcDefectCategory.Unknown"/> - never a real claim any call
    /// writes into a prompt, only ever a local "couldn't parse" sentinel.</summary>
    public static string ToToken(QcDefectCategory category) => category switch
    {
        QcDefectCategory.None => "NONE",
        QcDefectCategory.GarbledNumber => "GARBLED_NUMBER",
        QcDefectCategory.DomainTerm => "DOMAIN_TERM",
        QcDefectCategory.LostIdiom => "LOST_IDIOM",
        QcDefectCategory.UntranslatedPinyin => "UNTRANSLATED_PINYIN",
        QcDefectCategory.DroppedContent => "DROPPED_CONTENT",
        QcDefectCategory.DroppedStutter => "DROPPED_STUTTER",
        QcDefectCategory.HardToParseSeam => "HARD_TO_PARSE_SEAM",
        QcDefectCategory.OtherNamedDefect => "OTHER_NAMED_DEFECT",
        QcDefectCategory.MeaningReversal => "MEANING_REVERSAL",
        QcDefectCategory.Uncertain => "UNCERTAIN",
        QcDefectCategory.UnnaturalPhrasing => "UNNATURAL_PHRASING",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "QcDefectCategory.Unknown has no wire token."),
    };

    /// <summary>
    /// Parses a comma-separated token list as every QC response line that names defects uses it
    /// (detection's DEFECTS:, verification's UNRESOLVED:/NEW_DEFECTS:). A value that is exactly
    /// <c>NONE</c> succeeds with an empty list; otherwise every token must be a distinct, real
    /// category - <c>NONE</c> mixed into a list, an unrecognised token, or a duplicate is a protocol
    /// violation and fails the whole list, as does a list with no tokens at all.
    /// </summary>
    public static bool TryParseList(string value, out List<QcDefectCategory> categories)
    {
        categories = [];
        var trimmed = value.Trim();
        if (trimmed.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var token in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                return false;

            var category = Parse(token);
            if (category is QcDefectCategory.Unknown or QcDefectCategory.None)
                return false;

            if (categories.Contains(category))
                return false;

            categories.Add(category);
        }

        return categories.Count > 0;
    }
}
