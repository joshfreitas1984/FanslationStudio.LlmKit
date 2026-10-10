using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Tests.Utility;

/// <summary>
/// Pins the single QC vocabulary: "Quality Control" / <c>QualityControl*</c> / <c>Qc*</c>. The old
/// spellings stay banned in source, config, docs and skills (ADR history and the legacy-key shim
/// are allow-listed).
/// </summary>
public class QcVocabularyLintTests
{
    private static readonly Regex Banned = new(
        @"quality[ _-]?review|QualityEvaluator|quality[ _-]?check(?!s?\w)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] TextExtensions = [".cs", ".md", ".yaml", ".yml", ".txt", ".json", ".csproj", ".slnx"];

    // Path suffixes (forward slashes) that may keep the old spelling.
    private static readonly string[] AllowedPaths =
    [
        "docs/architecture/decisions/",
        "FanslationStudio.LlmKit.Assessments/Files/GoldSets/", // human review notes keep the names of the day
        "docs/features/translation-pipeline/quality-control-pass.md", // Terminology section names the banned terms
        "Tests/Utility/QcVocabularyLintTests.cs",
        "Tests/Configuration/LegacyQualityReviewKeyTests.cs",
        "FanslationStudio.LlmKit/Configuration/LlmConfig.cs",
        "FanslationStudio.LlmKit/Configuration/ConfigurationExtensions.cs",
        ".github/copilot-instructions.md",
    ];

    [Fact(DisplayName = "QC vocabulary - no banned spellings in source, docs or skills")]
    public void NoBannedSpellings()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.Split('/').Any(p => p is "bin" or "obj" or ".git" or ".vs" or "TestResults")) continue;
            if (!TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            if (AllowedPaths.Any(rel.StartsWith)) continue;

            if (Banned.IsMatch(rel)) offenders.Add($"{rel} (path)");
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                if (Banned.IsMatch(line)) offenders.Add($"{rel}:{lineNo}");
            }
        }

        Assert.True(offenders.Count == 0, "Banned QC spelling found:\n" + string.Join("\n", offenders.Take(50)));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root (AGENTS.md) not found.");
    }
}
