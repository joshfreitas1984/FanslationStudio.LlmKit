namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// Outcome of the post-translation quality review pass (<see cref="Workflow.QualityReviewWorkflow"/>)
/// for a <see cref="TranslationSplit"/>/column. See
/// docs/plans/quality-review-pass.md (DragonHierOverLlm repo) for the full design.
/// </summary>
public enum QcStatus
{
    /// <summary>Never reviewed by the QC pass, or the underlying translation changed since the
    /// last review (see <see cref="TranslationSplit.QcReviewedText"/>).</summary>
    NotReviewed,

    /// <summary>Reviewed - no correction needed.</summary>
    Passed,

    /// <summary>Reviewed - a correction was proposed AND passed the validation gate. See
    /// <see cref="TranslationSplit.QcTranslated"/> for the accepted correction.</summary>
    Corrected,

    /// <summary>Reviewed - a correction was proposed but rejected by the validation gate. The
    /// original <see cref="TranslationSplit.Translated"/>/<see cref="TranslationSplit.QcTranslated"/>
    /// is left untouched. See <see cref="TranslationSplit.QcRejectedCorrection"/>/
    /// <see cref="TranslationSplit.QcFailureReason"/> for what was proposed and why it was
    /// rejected.</summary>
    FailedValidation,
}
