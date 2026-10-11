using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Outcome of resolving one field's packaged text. <see cref="Text"/> is null when nothing usable
/// can be packaged. <see cref="QcAnchor"/> is set only when the field packaged its anchor's fresh,
/// score-gate-passing <see cref="TranslationSplit.QcTranslated"/>; <see cref="TranslatedFragments"/>
/// only when it was rebuilt from per-fragment translations. <see cref="Detail"/> says why a field
/// failed when it is more than the reason alone (a slot whose value cannot be restored).
/// </summary>
internal readonly record struct FieldResolution(
    string? Text,
    PackagingFailureReason Reason,
    TranslationSplit? QcAnchor = null,
    List<string>? TranslatedFragments = null,
    string? Detail = null);

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
    /// The split's source text as the game has it: its slots (<c>{npcName}</c>) replaced by the tokens they stood for. What
    /// packaging hands to game hooks and callbacks as the raw text; the same string as <see cref="TranslationSplit.Text"/> when
    /// the split has no tokens.
    /// </summary>
    public static string SourceText(TranslationSplit split) => TokenSlotter.RestoreSource(split.Text, split.Tokens);

    /// <summary>
    /// Puts the game's own form back for every slot of <paramref name="slotted"/>, the one place values are restored. Null, with the
    /// reason in <paramref name="detail"/>, when a slot cannot be resolved (no tokens entry, a glossary token with no glossary
    /// entry, an unconfigured name) so the caller can fall back instead of packaging a literal <c>{slot}</c>.
    /// </summary>
    public static string? RestoreSlots(string slotted, IReadOnlyDictionary<string, TokenValue> tokens, out string? detail)
    {
        detail = null;
        var slotter = StringTokenReplacer.Slotter;
        if (!slotter.HasTokens && tokens.Count == 0)
            return slotted;

        var restored = slotter.Restore(slotted, tokens);
        if (!restored.Ok)
        {
            detail = $"cannot restore {string.Join(", ", restored.Unresolved.Select(name => $"{{{name}}}"))} ({tokens.Count} token(s) stored)";
            return null;
        }

        if (slotter.HasTokens && slotter.FindUnrestoredSlot(restored.Text) is string left)
        {
            detail = $"{left} has no tokens entry";
            return null;
        }

        return restored.Text;
    }

    private static FieldResolution Failed(TranslationSplit split, string? detail)
    {
        Console.WriteLine($"Packaging: {detail} in \"{split.Text}\"");
        return new FieldResolution(null, PackagingFailureReason.RawFallback, Detail: detail);
    }

    /// <summary>Every fragment's slot values, and the template's own, as one map: slot names are numbered across a whole cell.</summary>
    public static IReadOnlyDictionary<string, TokenValue> UnionTokens(IReadOnlyList<TranslationSplit> fragments, FieldTemplate? template = null)
    {
        if (fragments.All(f => f.Tokens.Count == 0) && template is not { Tokens.Count: > 0 })
            return fragments[0].Tokens;

        var union = new Dictionary<string, TokenValue>();
        foreach (var token in fragments.SelectMany(f => f.Tokens).Concat(template?.Tokens ?? []))
            union.TryAdd(token.Key, token.Value);
        return union;
    }

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

        // A whole-cell QC correction is in slot space too; if a slot in it cannot be restored the plain fragments below are tried instead.
        if (qcFresh && !qcRejected && !string.IsNullOrEmpty(anchor!.QcTranslated)
            && RestoreSlots(anchor.QcTranslated, UnionTokens(fragments, template), out _) is string restoredQc)
            return new FieldResolution(restoredQc, PackagingFailureReason.None, QcAnchor: anchor);

        var translatedFragments = new List<string>(fragments.Count);

        foreach (var fragment in fragments)
        {
            if (!textFile.PackageOutput || fragment.FlaggedForRetranslation || !fragment.SafeToTranslate)
                return new FieldResolution(null, PackagingFailureReason.RawFallback);

            if (!string.IsNullOrEmpty(fragment.Translated))
            {
                var restored = RestoreSlots(fragment.Translated, fragment.Tokens, out var detail);
                if (restored == null)
                    return Failed(fragment, detail);

                translatedFragments.Add(transform == null ? restored : transform(SourceText(fragment), restored));
            }
            else if (!string.IsNullOrEmpty(fragment.Text))
                return new FieldResolution(null, PackagingFailureReason.RawFallback);
            else
                translatedFragments.Add(fragment.Text);
        }

        var templateText = template?.Template;
        if (template != null)
        {
            templateText = RestoreSlots(template.Template, template.Tokens, out var templateDetail);
            if (templateText == null)
                return Failed(fragments[0], templateDetail);
        }

        var text = template != null
            ? CompoundFieldSplitter.Reconstruct(templateText!, translatedFragments)
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

        var restored = RestoreSlots(effectiveTranslated, split.Tokens, out var detail);
        if (restored == null)
            return Failed(split, detail);

        return new FieldResolution(
            transform == null ? restored : transform(SourceText(split), restored),
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
