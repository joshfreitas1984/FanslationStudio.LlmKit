using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Shared logic for computing a column's "effective translated text" and for checking whether a
/// previously recorded quality-review outcome is still fresh relative to the column's CURRENT
/// Translated value(s) - used by both <see cref="Workflow.QualityReviewWorkflow"/> (to decide
/// whether a column needs re-reviewing) and every packaging path (to decide whether
/// <see cref="TranslationSplit.QcTranslated"/>/<see cref="TranslationSplit.QcQualityScore"/> can
/// still be trusted, or must be treated as if the column had never been reviewed).
///
/// Why this matters: nothing resets a column's Qc* fields when its Translated value changes for a
/// reason unrelated to the quality review pass itself (e.g. a glossary change flags a split via
/// <see cref="Workflow.TranslationWorkflow.ApplyAllRulesToCurrentTranslation"/> and it gets
/// retranslated, or a re-export/merge brings in a new Raw). Without this freshness check,
/// packaging would keep preferring a QC correction/score that describes a translation which no
/// longer exists, until the next quality-review-pass run happens to notice and refresh it - a real
/// risk of silently shipping stale, unrelated text. See docs/plans/quality-review-pass.md
/// (DragonHierOverLlm repo).
/// </summary>
public static class QualityReviewHelpers
{
    /// <summary>
    /// The exact text a quality review is/was performed against for one column: for a templated
    /// (compound) column, the fully reconstructed cell from every fragment's current
    /// <see cref="TranslationSplit.Translated"/>; for a plain column, just <paramref name="anchor"/>'s
    /// own <see cref="TranslationSplit.Translated"/>.
    /// </summary>
    public static string ComputeEffectiveTranslatedText(TranslationSplit anchor, FieldTemplate? template, IReadOnlyList<TranslationSplit> fragments)
    {
        return template != null
            ? CompoundFieldSplitter.Reconstruct(template.Template, fragments.Select(f => f.Translated).ToList())
            : anchor.Translated;
    }

    /// <summary>
    /// True if <paramref name="anchor"/>'s recorded quality-review outcome
    /// (<see cref="TranslationSplit.QcStatus"/>/<see cref="TranslationSplit.QcTranslated"/>/
    /// <see cref="TranslationSplit.QcQualityScore"/>) still describes the column's CURRENT
    /// effective translated text. False for a never-reviewed column, and false for a column whose
    /// Translated has changed (e.g. retranslated) since it was last reviewed - in both cases every
    /// Qc*-derived field must be ignored by the caller (treated exactly like an unreviewed column)
    /// rather than trusted. Also false for a <see cref="TranslationSplit.QcTranslated"/> that still
    /// carries a leaked "CORRECTED:" label (see <see cref="IsCorrectedLabelLeak"/>) - every packaging
    /// path calls this before trusting QcTranslated, so this is the single choke point that keeps a
    /// corrupted correction like "Wealth in the millions CORRECTED: NONE" from ever shipping, even if
    /// it somehow got written by a path other than <see cref="Workflow.QualityReviewWorkflow"/>'s own
    /// (already-guarded) parsing.
    /// </summary>
    public static bool IsQcReviewFresh(TranslationSplit anchor, FieldTemplate? template, IReadOnlyList<TranslationSplit> fragments)
    {
        if (anchor.QcStatus == QcStatus.NotReviewed)
            return false;

        if (IsCorrectedLabelLeak(anchor.QcTranslated))
            return false;

        return anchor.QcReviewedText == ComputeEffectiveTranslatedText(anchor, template, fragments);
    }

    /// <summary>
    /// True if <paramref name="qcTranslated"/> still contains a literal "CORRECTED:" label - the
    /// signature of the correction-suffix-leak bug in <see cref="Workflow.QualityReviewWorkflow"/>'s
    /// response parsing (see its <c>CorrectedLineRegex</c> doc comment), which once produced a stored
    /// value like "Wealth in the millions CORRECTED: NONE" instead of the clean correction. A value
    /// like this is never a legitimate translation on its own merits, regardless of which code path
    /// wrote it.
    /// </summary>
    public static bool IsCorrectedLabelLeak(string? qcTranslated) =>
        !string.IsNullOrEmpty(qcTranslated) && qcTranslated.Contains("CORRECTED:", StringComparison.OrdinalIgnoreCase);
}
