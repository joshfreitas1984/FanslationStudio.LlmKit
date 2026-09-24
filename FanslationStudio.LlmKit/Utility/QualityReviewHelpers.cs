using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;

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
    public static bool IsQcReviewFresh(TranslationSplit anchor, FieldTemplate? template, IReadOnlyList<TranslationSplit> fragments, QualityReviewConfig qualityReview)
    {
        // Single choke point every packaging path checks before trusting any Qc*-derived field -
        // flipping `qualityReview.enabled: false` and re-packaging (no LLM calls, no re-running QC)
        // makes every column package as if QC had never run: plain pre-QC Translated text, with
        // minAcceptableScore/autoAcceptDefectCategories never consulted. See
        // docs/plans/quality-review-pass.md (DragonHierOverLlm repo).
        if (!qualityReview.Enabled)
            return false;

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

    /// <summary>
    /// True if a column's recorded QC score should be trusted for packaging - either it cleared
    /// <see cref="QualityReviewConfig.MinAcceptableScore"/>, or it didn't but its
    /// <see cref="TranslationSplit.QcDefectCategory"/> is one a human has hand-validated and
    /// designated low-precision enough to auto-accept wholesale via
    /// <see cref="QualityReviewConfig.AutoAcceptDefectCategories"/> (see that property's doc
    /// comment and docs/qc-qualityscore-noise-investigation.md's "stratify by DEFECT category"
    /// policy step). The single choke point every packaging path (CsvGameDataWorkflow,
    /// JsonGameDataWorkflow, DynamicStringWorkflow, PrefabTextWorkflow) uses for this decision, so
    /// the policy only needs to be taught here once. A null <paramref name="score"/> (never
    /// reviewed, or a rejected correction with the score already cleared) always passes - callers
    /// already gate those cases separately via <see cref="IsQcReviewFresh"/> and a
    /// non-empty-<see cref="TranslationSplit.QcTranslated"/> check.
    /// </summary>
    public static bool PassesQcScoreGate(int? score, QcDefectCategory category, QualityReviewConfig qualityReview)
    {
        if (score is not int s || s >= qualityReview.MinAcceptableScore)
            return true;

        return qualityReview.AutoAcceptDefectCategories.Contains(category);
    }

    /// <summary>
    /// The fragment that actually carries a column's QC state - see the <c>SubIndex == 0</c> anchor
    /// convention (docs/features/translation-pipeline/quality-review-pass.md, FanslationStudio.LlmKit repo):
    /// a templated/compound column's whole-cell <see cref="TranslationSplit.QcStatus"/>/
    /// <see cref="TranslationSplit.QcTranslated"/>/<see cref="TranslationSplit.QcQualityScore"/>/
    /// <see cref="TranslationSplit.QcReviewedText"/> live entirely on that column's
    /// <c>SubIndex == 0</c> fragment, never on <c>SubIndex >= 1</c> fragments. <paramref name="split"/>
    /// itself IS that anchor for a plain (single-fragment) column, but for a compound column whose
    /// changed fragment is <c>SubIndex >= 1</c>, calling <see cref="TranslationSplit.ResetQcState"/>
    /// on <paramref name="split"/> directly resets fields that never held any real QC data, leaving
    /// the anchor's stale <c>Passed</c>/<c>Corrected</c> state untouched until the next QC run's own
    /// <see cref="IsQcReviewFresh"/> dynamic recompute catches up. Groups <paramref name="line"/>'s
    /// splits by <see cref="QualityReviewWorkflow.ColumnKey(TranslationSplit)"/> - the same
    /// SplitPath-for-JSON/Split-for-everything-else key <see cref="Workflow.QualityReviewWorkflow"/>
    /// and every packaging path already use - so this resolves the anchor identically for CSV,
    /// PrefabText, DynamicStrings, and JSON field-path columns alike. Shared by every caller that
    /// mutates a split's <see cref="TranslationSplit.Translated"/> outside the quality review pass
    /// itself and wants the anchor's stale Qc* fields to clear immediately in
    /// <c>Files/Converted/*.yaml</c> rather than only at the next QC run's freshness recompute (see
    /// <see cref="Workflow.TranslationWorkflow.UpdateSplit"/> and <see cref="TranslationService"/>'s
    /// retranslation paths).
    /// </summary>
    public static TranslationSplit FindQcAnchor(TranslationLine line, TranslationSplit split)
    {
        var key = QualityReviewWorkflow.ColumnKey(split);
        TranslationSplit? anchor = null;
        TranslationSplit? first = null;

        foreach (var candidate in line.Splits)
        {
            if (QualityReviewWorkflow.ColumnKey(candidate) != key)
                continue;

            first ??= candidate;
            if (candidate.SubIndex == 0)
            {
                anchor = candidate;
                break;
            }
        }

        return anchor ?? first ?? split;
    }
}
