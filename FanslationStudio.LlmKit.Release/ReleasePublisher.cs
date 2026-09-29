using System.Diagnostics;

namespace FanslationStudio.LlmKit.Release;

/// <summary>
/// Publishing helpers. Nothing here uses ambient GitHub auth: the default is a prefilled browser URL,
/// and the optional gh path passes a token for one named account to the child process only.
/// Failures are returned, not thrown, so packaging never fails because of GitHub.
/// </summary>
public static class ReleasePublisher
{
    public const string NotesFileName = "release-notes.md";

    // Browsers/GitHub reject very long URLs; past this the notes file is used instead of the body param.
    const int MaxBodyChars = 1500;

    public static string BuildReleaseUrl(string ownerAndRepo, string version, string? notes = null)
    {
        var url = $"https://github.com/{ownerAndRepo}/releases/new?tag={Uri.EscapeDataString(version)}&title={Uri.EscapeDataString(version)}";
        if (!string.IsNullOrWhiteSpace(notes) && notes.Length <= MaxBodyChars)
            url += $"&body={Uri.EscapeDataString(notes)}";
        return url;
    }

    /// <summary>Opens a folder or URL with the shell. Returns false if it could not be opened.</summary>
    public static bool TryOpen(string pathOrUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a GitHub release with <c>gh release create</c> as <paramref name="ghAccount"/>.
    /// Returns null on success, otherwise the reason the caller should fall back to the URL.
    /// </summary>
    public static string? TryPublishWithGh(string ownerAndRepo, string ghAccount, string version, IEnumerable<string> assets, string? notesPath = null)
    {
        try
        {
            var (tokenExit, token, tokenErr) = Run("gh", ["auth", "token", "--user", ghAccount], null);
            if (tokenExit != 0 || string.IsNullOrWhiteSpace(token))
                return $"gh has no login for '{ghAccount}': {tokenErr.Trim()}";

            var args = new List<string> { "release", "create", version, "--repo", ownerAndRepo, "--title", version };
            if (notesPath != null)
                args.AddRange(["--notes-file", notesPath]);
            else
                args.Add("--generate-notes");
            args.AddRange(assets);

            var (exit, _, err) = Run("gh", args, token.Trim());
            return exit == 0 ? null : $"gh release create failed: {err.Trim()}";
        }
        catch (Exception ex)
        {
            return $"gh unavailable: {ex.Message}";
        }
    }

    static (int ExitCode, string Output, string Error) Run(string exe, IEnumerable<string> args, string? ghToken)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        if (ghToken != null)
            psi.Environment["GH_TOKEN"] = ghToken;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr.Result);
    }
}
