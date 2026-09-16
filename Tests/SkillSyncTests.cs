namespace Tests;

/// <summary>
/// "Update Local Skills" - copies this repo's canonical .claude/skills/* into each downstream
/// repo's .claude/skills/. This repo is the single source of truth for skills shared across
/// DragonHierOverLlm, LegendOfMortalOverLlm, and WanXiangOverLlm (Claude Code loads skills from
/// the working directory's own repo, so a copy has to exist locally in each one). Not part of the
/// default test run - re-run explicitly whenever a skill under .claude/skills/ changes:
/// dotnet test --filter Category=SkillSync
/// </summary>
public class SkillSyncTests
{
    private static readonly string[] DownstreamRepos =
    [
        "DragonHierOverLlm",
        "LegendOfMortalOverLlm",
        "WanXiangOverLlm",
    ];

    [Fact(DisplayName = "Update Local Skills - sync .claude/skills to downstream repos")]
    [Trait("Category", "SkillSync")]
    public void UpdateLocalSkills()
    {
        var repoRoot = FindRepoRoot();
        var sourceSkillsDir = Path.Combine(repoRoot, ".claude", "skills");
        Assert.True(Directory.Exists(sourceSkillsDir), $"Canonical skills directory not found: {sourceSkillsDir}");

        var reposParent = Path.GetDirectoryName(repoRoot)!;
        var copiedFiles = new List<string>();

        foreach (var downstreamRepoName in DownstreamRepos)
        {
            var downstreamRepoPath = Path.Combine(reposParent, downstreamRepoName);
            Assert.True(Directory.Exists(downstreamRepoPath), $"Downstream repo not found next to this one: {downstreamRepoPath}");

            var targetSkillsDir = Path.Combine(downstreamRepoPath, ".claude", "skills");
            Directory.CreateDirectory(targetSkillsDir);

            foreach (var sourceSkillDir in Directory.GetDirectories(sourceSkillsDir))
            {
                var skillName = Path.GetFileName(sourceSkillDir);
                var targetSkillDir = Path.Combine(targetSkillsDir, skillName);

                foreach (var sourceFile in Directory.GetFiles(sourceSkillDir, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(sourceSkillDir, sourceFile);
                    var targetFile = Path.Combine(targetSkillDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                    File.Copy(sourceFile, targetFile, overwrite: true);
                    copiedFiles.Add($"{downstreamRepoName}/.claude/skills/{skillName}/{relativePath}");
                }
            }
        }

        Assert.NotEmpty(copiedFiles);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !Directory.Exists(Path.Combine(current.FullName, ".claude")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current!.FullName;
    }
}
