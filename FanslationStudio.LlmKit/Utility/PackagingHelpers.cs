using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Outcome of resolving one field's packaged text. <see cref="Text"/> is null when nothing usable
/// can be packaged. <see cref="QcAnchor"/> is set only when the field packaged its anchor's fresh,
/// score-gate-passing <see cref="TranslationSplit.QcTranslated"/>; <see cref="TranslatedFragments"/>
/// only when it was rebuilt from per-fragment translations.
/// </summary>
internal readonly record struct FieldResolution(
    string? Text,
    PackagingFailureReason Reason,
    TranslationSplit? QcAnchor = null,
    List<string>? TranslatedFragments = null);

/// <summary>
/// The Converted -&gt; Mod field resolution shared by every packaging workflow
/// (<see cref="Workflow.CsvGameDataWorkflow"/>, <see cref="Workflow.JsonGameDataWorkflow"/>,
/// <see cref="Workflow.PrefabTextWorkflow"/>, <see cref="Workflow.DynamicStringWorkflow"/>). Neither
/// method applies <see cref="PackagingTextFixups"/> - each workflow does that with its own raw text
/// and column. See docs/features/translation-pipeline/quality-control-pass.md for the QC gating rules.
/// </summary>
internal static class PackagingHelpers
{
    /// <summary>
    /// Resolves a field made of <paramref name="fragments"/> (ordered by SubIndex). A fresh,
    /// score-gate-passing whole-cell QC correction on the anchor wins outright; otherwise every
    /// fragment must be packageable and translated (an empty-Text fragment passes through as-is) and
    /// the result is reconstructed via <paramref name="template"/>, or is the first fragment's
    /// translation when there is no template. A fresh QC correction held back by the score gate
    /// reports <see cref="PackagingFailureReason.QcRejected"/> alongside the reconstructed text - it
    /// never discards a usable pre-QC translation.
    /// </summary>
    /// <param name="anchorFallsBackToFirst">The QC anchor is the SubIndex == 0 fragment; when true and
    /// there is none, the first fragment is used instead.</param>
    /// <param name="transform">Optional (sourceText, translated) rewrite of each translated fragment.</param>
    public static FieldResolution ResolveFragments(
        IReadOnlyList<TranslationSplit> fragments, FieldTemplate? template, TextFileToSplit textFile,
        QualityControlConfig qualityControl, bool anchorFallsBackToFirst, Func<string, string, string>? transform = null)
    {
        var anchor = fragments.FirstOrDefault(f => f.SubIndex == 0)
            ?? (anchorFallsBackToFirst ? fragments.FirstOrDefault() : null);

        // Whole-cell QC state lives only on the anchor, and is only trusted while still fresh
        // relative to the fragments' current Translated values.
        var qcFresh = anchor != null && QualityControlHelpers.IsQcReviewFresh(anchor, template, fragments, qualityControl);
        var qcRejected = qcFresh && !QualityControlHelpers.PassesQcScoreGate(anchor!.QcQualityScore, anchor.QcDefectCategory, qualityControl);

        if (qcFresh && !qcRejected && !string.IsNullOrEmpty(anchor!.QcTranslated))
            return new FieldResolution(anchor.QcTranslated, PackagingFailureReason.None, QcAnchor: anchor);

        var translatedFragments = new List<string>(fragments.Count);

        foreach (var fragment in fragments)
        {
            if (!textFile.PackageOutput || fragment.FlaggedForRetranslation || !fragment.SafeToTranslate)
                return new FieldResolution(null, PackagingFailureReason.RawFallback);

            if (!string.IsNullOrEmpty(fragment.Translated))
                translatedFragments.Add(transform == null ? fragment.Translated : transform(fragment.Text, fragment.Translated));
            else if (!string.IsNullOrEmpty(fragment.Text))
                return new FieldResolution(null, PackagingFailureReason.RawFallback);
            else
                translatedFragments.Add(fragment.Text);
        }

        var text = template != null
            ? CompoundFieldSplitter.Reconstruct(template.Template, translatedFragments)
            : translatedFragments[0];

        return new FieldResolution(text, qcRejected ? PackagingFailureReason.QcRejected : PackagingFailureReason.None,
            TranslatedFragments: translatedFragments);
    }

    /// <summary>
    /// Resolves a plain (non-templated) split: its fresh, score-gate-passing QcTranslated if any,
    /// otherwise its Translated - packaged only when non-empty and the split is neither flagged for
    /// retranslation nor unsafe. Does not consult <see cref="TextFileToSplit.PackageOutput"/>.
    /// </summary>
    public static FieldResolution ResolvePlainSplit(
        TranslationSplit split, QualityControlConfig qualityControl, Func<string, string, string>? transform = null)
    {
        var qcFresh = QualityControlHelpers.IsQcReviewFresh(split, null, [split], qualityControl);
        var qcRejected = qcFresh && !QualityControlHelpers.PassesQcScoreGate(split.QcQualityScore, split.QcDefectCategory, qualityControl);
        var useQc = qcFresh && !qcRejected && !string.IsNullOrEmpty(split.QcTranslated);
        var effectiveTranslated = useQc ? split.QcTranslated : split.Translated;

        if (string.IsNullOrEmpty(effectiveTranslated) || split.FlaggedForRetranslation || !split.SafeToTranslate)
            return new FieldResolution(null, PackagingFailureReason.RawFallback);

        return new FieldResolution(
            transform == null ? effectiveTranslated : transform(split.Text, effectiveTranslated),
            qcRejected ? PackagingFailureReason.QcRejected : PackagingFailureReason.None,
            QcAnchor: useQc ? split : null);
    }
}

/// <summary>
/// Per-file packaging tallies in the (Passed, QcRejected, RawFallback) shape every Package*Async
/// method returns.
/// </summary>
internal sealed class PackagingCounts
{
    public int Passed { get; set; }
    public int QcRejected { get; set; }
    public int RawFallback { get; set; }

    /// <summary>
    /// Counts one outcome. A <see cref="PackagingFailureReason.None"/> outcome only counts as
    /// passed when something was actually packaged.
    /// </summary>
    public void Record(PackagingFailureReason reason, bool packaged)
    {
        switch (reason)
        {
            case PackagingFailureReason.QcRejected:
                QcRejected++;
                break;
            case PackagingFailureReason.RawFallback:
                RawFallback++;
                break;
            case PackagingFailureReason.None when packaged:
                Passed++;
                break;
        }
    }

    public (int Passed, int QcRejected, int RawFallback) ToTuple() => (Passed, QcRejected, RawFallback);
}
