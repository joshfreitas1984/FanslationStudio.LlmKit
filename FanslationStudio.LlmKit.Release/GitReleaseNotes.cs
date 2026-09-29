using System.Diagnostics;
using System.Text;

namespace FanslationStudio.LlmKit.Release;

/// <summary>
/// Builds a markdown change list from the git commits between the newest local tag and HEAD.
/// Uses local tags unless <c>fetchTags</c> is set; releases tagged on GitHub are not local until fetched.
/// Throws with git's message on failure; callers decide whether that is fatal.
/// </summary>
public static class GitReleaseNotes
{
    public static string Generate(string repoFolder, int maxCommits = 200, bool fetchTags = false)
    {
        // Read-only; a failure (offline, no remote) just means we use the tags already local.
        if (fetchTags)
            TryGit(repoFolder, "fetch", "--tags", "--quiet");

        // Not `git describe`: it picks arbitrarily when several tags share a commit. Version-named tags sort by name,
        // and only tags starting with a digit count: the rolling "installer" release tag must never be "the last release".
        var lastTag = Git(repoFolder, "tag", "--merged", "HEAD", "--list", "[0-9]*", "--sort=-version:refname")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        var range = string.IsNullOrEmpty(lastTag) ? "HEAD" : $"{lastTag}..HEAD";

        var log = Git(repoFolder, "log", range, "--no-merges", $"--max-count={maxCommits}", "--pretty=format:%h\t%s");
        var sb = new StringBuilder();
        sb.AppendLine("## Changes");
        sb.AppendLine();

        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            sb.AppendLine("- No commits since the last release.");

        foreach (var line in lines)
        {
            var parts = line.Split('\t', 2);
            sb.AppendLine(parts.Length == 2 ? $"- {parts[1].Trim()} ({parts[0]})" : $"- {line.Trim()}");
        }

            return sb.ToString().TrimEnd();
    }

    static string? TryGit(string workingDirectory, params string[] args)
    {
        try
        {
            return Git(workingDirectory, args);
        }
        catch
        {
            return null;
        }
    }

    // Returns trimmed stdout; throws with git's own message when git is missing or exits non-zero.
    static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // A repo owned by another account (e.g. Administrators) makes git refuse with "dubious ownership".
        // Trust just this folder for this one command instead of editing the user's global git config.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"safe.directory={Path.GetFullPath(workingDirectory).Replace('\\', '/')}");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {workingDirectory}: {stderr.Result.Trim()}");
        return stdout.Trim();
    }
}
