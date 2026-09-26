namespace FanslationStudio.LlmKit.Support;

public sealed record QcDefectFinding(QcDefectCategory Category);

/// <summary>
/// Why a detection call produced no usable result. Every non-<see cref="None"/> value maps to the
/// same <c>Success = false</c> (the pipeline treats them identically - skip and retry next run), but
/// they have very different fixes: <see cref="RequestError"/>/<see cref="Truncated"/> almost always
/// mean the prompt outgrew the model's <c>num_ctx</c>, while <see cref="ParseError"/> is a genuine
/// protocol violation by the model.
/// </summary>
public enum QcDetectionFailureKind
{
    None,
    /// <summary>The HTTP call itself failed (non-2xx, e.g. a context-size 400, or a timeout).</summary>
    RequestError,
    /// <summary>The server stopped generating because it hit a length/context limit
    /// (<c>done_reason</c>/<c>finish_reason</c> "length") and the cut-off response didn't parse.</summary>
    Truncated,
    /// <summary>The model finished normally but its response didn't match the DEFECTS protocol.</summary>
    ParseError,
}

public sealed record QcDetectionResult(
    bool Success,
    IReadOnlyList<QcDefectFinding> Findings,
    QcDetectionFailureKind FailureKind = QcDetectionFailureKind.None,
    string? FailureDetail = null)
{
    public bool HasDefects => Findings.Count > 0;

    public static QcDetectionResult Merge(params QcDetectionResult[] results)
    {
        var failed = results.FirstOrDefault(result => !result.Success);
        if (failed != null)
            return new QcDetectionResult(false, [], failed.FailureKind, failed.FailureDetail);

        var findings = new List<QcDefectFinding>();
        foreach (var result in results)
        {
            foreach (var finding in result.Findings)
            {
                if (findings.Any(existing => existing.Category == finding.Category))
                    continue;

                findings.Add(finding);
            }
        }

        return new QcDetectionResult(true, findings);
    }
}
