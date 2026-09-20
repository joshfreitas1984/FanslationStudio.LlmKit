using System.Linq;

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

    /// <summary>
    /// Combines two or more independent verify calls over the SAME candidate correction into one
    /// conservative verdict - the opposite merge direction from <see cref="QcDetectionResult.Merge"/>.
    /// Detection merges permissively (a defect either call names is kept, since missing a real
    /// defect is the worse failure); verification merges strictly (a problem either call names is
    /// kept as unresolved/new, since accepting a harmful correction is the worse failure here) - any
    /// single call flagging an issue is enough to reject the correction, even if another call judged
    /// it fine. Score is the minimum across calls, matching the same "any call's objection wins"
    /// logic, though <see cref="Accepted"/> never depends on score alone regardless.
    /// </summary>
    public static QcVerificationResult Merge(params QcVerificationResult[] results)
    {
        if (results.Any(result => !result.Success))
            return new QcVerificationResult(false, [], [], 0);

        var unresolved = new List<QcDefectCategory>();
        var newDefects = new List<QcDefectCategory>();
        foreach (var result in results)
        {
            foreach (var category in result.UnresolvedDefects)
            {
                if (!unresolved.Contains(category))
                    unresolved.Add(category);
            }
            foreach (var category in result.NewDefects)
            {
                if (!newDefects.Contains(category))
                    newDefects.Add(category);
            }
        }

        return new QcVerificationResult(true, unresolved, newDefects, results.Min(result => result.Score));
    }
}
