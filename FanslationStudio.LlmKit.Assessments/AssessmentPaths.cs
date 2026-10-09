namespace FanslationStudio.LlmKit.Assessments;

internal static class AssessmentPaths
{
    /// <summary>The host's working directory (<c>Files/</c> in the source tree, not the build output, so results and gold sets land in the repo).</summary>
    public static string WorkingDirectory { get; } = Find();

    public static string GoldSet => Path.Combine(WorkingDirectory, "GoldSets", "ChineseToEnglishWuxia.yaml");

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FanslationStudio.LlmKit.Assessments.csproj")))
            dir = dir.Parent;
        return dir == null
            ? throw new InvalidOperationException("Assessments project directory not found above the test output.")
            : Path.Combine(dir.FullName, "Files");
    }
}
