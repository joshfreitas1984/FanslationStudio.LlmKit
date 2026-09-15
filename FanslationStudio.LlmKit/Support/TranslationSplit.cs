using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Support;

public class TranslationSplit
{
    public int Split { get; set; } = 0;

    /// <summary>
    /// Path/name of the source field this split came from (e.g. a JSON property name, optionally
    /// with an array index suffix like "NameList[2]"), used to re-match a line's fields after a
    /// re-export by field identity rather than position. Empty for file types that don't need
    /// field-path matching (PrefabText, DynamicStrings, CSV columns, which use <see cref="Split"/>).
    /// </summary>
    public string SplitPath { get; set; } = string.Empty;

    /// <summary>
    /// Index of this fragment within its CSV column when the column is a compound field that was
    /// decomposed into multiple translatable fragments (see <see cref="FieldTemplate"/>). Zero for
    /// plain columns where the whole cell is a single split, preserving old behavior/serialized data.
    /// </summary>
    public int SubIndex { get; set; } = 0;

    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Text { get; set; } = string.Empty;

    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Translated { get; set; } = string.Empty;

    public bool SafeToTranslate { get; set; } = true;

    public bool FlaggedForRetranslation { get; set; } = false;

    //public bool FlaggedForGlossaryExtraction { get; set; } = true;

    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string FlaggedMistranslation { get; set; } = string.Empty;

    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string FlaggedHallucination { get; set; } = string.Empty;

    // --- Quality review pass fields (see docs/plans/quality-review-pass.md in DragonHierOverLlm) ---
    // Additive/optional, per the golden rule - all default to values that preserve old behavior
    // for any TranslationSplit that predates this feature (old serialized YAML deserializes with
    // QcStatus.NotReviewed and every Qc* string/nullable empty/null, i.e. "never reviewed").

    /// <summary>
    /// The exact "effective cell text" (see QualityReviewWorkflow - <see cref="Translated"/> for a
    /// plain column, or the reconstructed cell for a templated column) that was reviewed to
    /// produce the current <see cref="QcStatus"/>. A subsequent QC run skips this split/column (no
    /// LLM call) while its current effective text still equals this value; if the underlying
    /// translation changes later (re-translation, manual fix, glossary rerun), this no longer
    /// matches and the line is automatically picked up for review again - no manual invalidation
    /// needed.
    /// </summary>
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string QcReviewedText { get; set; } = string.Empty;

    /// <summary>
    /// QC-corrected replacement for <see cref="Translated"/>, set only when <see cref="QcStatus"/>
    /// is <see cref="QcStatus.Corrected"/>. Packaging prefers this over <see cref="Translated"/>
    /// when non-empty. For a plain (non-templated) column this is the split's own corrected whole
    /// cell text. For a templated/compound column, only the column's <c>SubIndex == 0</c> fragment
    /// ever carries a meaningful value here - it represents the whole reconstructed cell's QC
    /// correction (there is one QC verdict per column, not per fragment, since the QC pass reviews
    /// the fully reconstructed cell - see QualityReviewWorkflow). Other fragments in the same
    /// column (SubIndex >= 1) leave this empty.
    /// </summary>
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string QcTranslated { get; set; } = string.Empty;

    /// <summary>Outcome of the last quality review pass. See <see cref="Support.QcStatus"/>.</summary>
    public QcStatus QcStatus { get; set; } = QcStatus.NotReviewed;

    /// <summary>
    /// True when this split/column needs a human glance: either a QC-proposed correction was
    /// rejected by the validation gate (<see cref="QcStatus"/> == FailedValidation), or
    /// <see cref="QcQualityScore"/> fell below the configured minimum acceptable score. Recomputed
    /// on every QC run (like <see cref="FlaggedForRetranslation"/>) - not an append-only marker a
    /// human has to remember to clear.
    /// </summary>
    public bool FlaggedForQcReview { get; set; } = false;

    /// <summary>The corrected text QC proposed that got rejected by the validation gate, kept so a
    /// human reviewer can see exactly what was tried. Empty unless <see cref="QcStatus"/> ==
    /// FailedValidation.</summary>
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string QcRejectedCorrection { get; set; } = string.Empty;

    /// <summary>Why <see cref="QcRejectedCorrection"/> was rejected (reuses
    /// ValidationResult.CorrectionPrompt-style reason text). Empty unless <see cref="QcStatus"/>
    /// == FailedValidation.</summary>
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string QcFailureReason { get; set; } = string.Empty;

    /// <summary>
    /// 0-100 self-rated confidence, from the QC model itself, that the current
    /// <see cref="Translated"/>/<see cref="QcTranslated"/> value is an accurate, well-constructed
    /// translation of <see cref="Text"/>. Null means "not yet reviewed" (distinct from a real 0).
    /// Treat as a relative sort key for triage, not a calibrated absolute metric - see
    /// docs/plans/quality-review-pass.md's score-calibration caveat.
    /// </summary>
    public int? QcQualityScore { get; set; }

    /// <summary>
    /// The <c>DEFECT:</c> category the QC model named alongside <see cref="QcQualityScore"/> - see
    /// <see cref="Support.QcDefectCategory"/>. <see cref="Support.QcDefectCategory.Unknown"/> (the
    /// default) means either "never reviewed" or a response that predates the DEFECT-first prompt,
    /// same "not yet reviewed" convention as <see cref="QcQualityScore"/> being null.
    /// </summary>
    public QcDefectCategory QcDefectCategory { get; set; } = QcDefectCategory.Unknown;

    /// <summary>
    /// How many consecutive times <see cref="Workflow.QualityReviewWorkflow.ApplyRulesToCurrentQcTranslated"/>
    /// has reset this column's <see cref="QcTranslated"/> for breaking a rule, against the SAME
    /// underlying <see cref="Translated"/> baseline (see <see cref="QcRuleCheckFailureBaseline"/>).
    /// Once this reaches <see cref="Configuration.QualityReviewConfig.MaxRuleCheckRetries"/>, the
    /// column stops being retried automatically - its correction is still discarded like every
    /// other reset (packaging never trusts a rule-breaking QcTranslated), but <see cref="QcStatus"/>
    /// is left at <see cref="Support.QcStatus.FailedValidation"/> instead of
    /// <see cref="Support.QcStatus.NotReviewed"/>, so <see cref="Workflow.QualityReviewWorkflow.RunAsync"/>
    /// stops re-reviewing it and it's surfaced for a human instead
    /// (<see cref="Workflow.QualityReviewWorkflow.GetFlaggedQcReviews"/>), exactly like a
    /// freshly-rejected correction already is. Deliberately NOT cleared by
    /// <see cref="ResetQcState"/> - unlike every other Qc* field, this needs to survive the very
    /// reset it's counting, or it could never accumulate past 1. Never persists across an upstream
    /// change though: <see cref="QcRuleCheckFailureBaseline"/> not matching the column's current
    /// effective translated text means the count restarts from 0 - a retranslation, manual fix, or
    /// repair deserves a fresh retry budget, not one already exhausted by different text. A human
    /// can also explicitly clear this (see <see cref="Workflow.QualityReviewWorkflow.ResetQcRetryLimits"/>)
    /// if they've fixed the underlying cause (e.g. removed a false-positive bad word) and want
    /// previously given-up columns retried anyway.
    /// </summary>
    public int QcRuleCheckFailureCount { get; set; } = 0;

    /// <summary>The effective translated text <see cref="QcRuleCheckFailureCount"/> was last
    /// accumulated against - see its doc comment.</summary>
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string QcRuleCheckFailureBaseline { get; set; } = string.Empty;

    //public DateTime LastTranslatedOn = DateTime.Now;

    public TranslationSplit() { }

    public TranslationSplit(int split, string text)
    {
        Split = split;
        Text = text;
    }

    public TranslationSplit(int split, int subIndex, string text)
    {
        Split = split;
        SubIndex = subIndex;
        Text = text;
    }

    public void ResetFlags(bool translated = true)
    {
        //if (translated)
        //    LastTranslatedOn = DateTime.Now;

        FlaggedForRetranslation = false;
        FlaggedMistranslation = string.Empty;
        FlaggedHallucination = string.Empty;
    }

    /// <summary>
    /// Clears every quality-review-pass field back to "never reviewed" - called by
    /// <see cref="Workflow.QualityReviewWorkflow"/> immediately before recording a fresh review
    /// outcome, so a stale <see cref="QcTranslated"/>/<see cref="FlaggedForQcReview"/> from an
    /// earlier review of different text (e.g. before a retranslation changed <see cref="Translated"/>)
    /// never lingers alongside this review's result.
    /// </summary>
    public void ResetQcState()
    {
        QcTranslated = string.Empty;
        QcStatus = QcStatus.NotReviewed;
        QcReviewedText = string.Empty;
        FlaggedForQcReview = false;
        QcRejectedCorrection = string.Empty;
        QcFailureReason = string.Empty;
        QcQualityScore = null;
        QcDefectCategory = QcDefectCategory.Unknown;
    }

    //public void ResetGlossaryFlags()
    //{
    //    FlaggedForGlossaryExtraction = true;
    //}
}
