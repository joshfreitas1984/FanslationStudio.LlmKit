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
        "UNCERTAIN" => QcDefectCategory.Uncertain,
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
        QcDefectCategory.Uncertain => "UNCERTAIN",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "QcDefectCategory.Unknown has no wire token."),
    };
}
