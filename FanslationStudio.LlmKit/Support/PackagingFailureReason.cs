namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// Why a packaged line/field/row fell back to its original raw text instead of its translation -
/// see the various Package*Async workflow methods' (Passed, QcRejected, RawFallback) return counts.
/// Kept as a two-bucket split (rather than a reason per skip condition) to match what callers
/// actually need to report separately: a translation the quality review pass explicitly rejected,
/// versus everything else that made it ineligible to package (missing/unsafe/flagged-for-retranslation
/// translation, or PackageOutput disabled for the file).
/// </summary>
public enum PackagingFailureReason
{
    /// <summary>Packaged successfully - not a failure.</summary>
    None,

    /// <summary>The quality review pass scored the translation below MinAcceptableScore.</summary>
    QcRejected,

    /// <summary>Fell back to raw for any other reason.</summary>
    RawFallback,
}
