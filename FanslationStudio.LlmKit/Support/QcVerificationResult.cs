namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// Call 4's result - whether a call-3-drafted (or call-5-repaired) candidate correction actually
/// resolves every confirmed defect from call 1/2's merged detection, and whether it introduces any
/// new one. Never a single DEFECT/SCORE pair like the old two-call protocol used - a confirmed
/// defect set can hold more than one category, so the verifier reports per-defect resolution rather
/// than collapsing everything into one token.
/// </summary>
public sealed record QcVerificationResult(
    bool Success,
    IReadOnlyList<QcDefectCategory> UnresolvedDefects,
    IReadOnlyList<QcDefectCategory> NewDefects,
    int Score)
{
    public bool AllResolved => UnresolvedDefects.Count == 0;
    public bool IntroducedNewDefect => NewDefects.Count > 0;

    /// <summary>The correction is accepted only when the verifier ran successfully, every confirmed
    /// defect is resolved, and nothing new was introduced - never on score alone.</summary>
    public bool Accepted => Success && AllResolved && !IntroducedNewDefect;
}
