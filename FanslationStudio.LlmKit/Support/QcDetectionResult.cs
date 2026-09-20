namespace FanslationStudio.LlmKit.Support;

public sealed record QcDefectFinding(QcDefectCategory Category);

public sealed record QcDetectionResult(
    bool Success,
    IReadOnlyList<QcDefectFinding> Findings)
{
    public bool HasDefects => Findings.Count > 0;

    public static QcDetectionResult Merge(params QcDetectionResult[] results)
    {
        if (results.Any(result => !result.Success))
            return new QcDetectionResult(false, []);

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