using System.Diagnostics;

namespace FanslationStudio.Installer.Core;

/// <summary>
/// `--apply-update &lt;zip&gt; --pid &lt;gamePid&gt; [--game-dir &lt;dir&gt;] [--steam-app-id &lt;id&gt;]`, launched detached by the in-game
/// plugin. The updater lives in BepInEx/updater inside the game folder, so when GameDir isn't given it is derived from
/// the updater's own location.
/// </summary>
public record UpdateCommand(string ZipPath, int? Pid, string? GameDir, int? SteamAppId, bool Relocated)
{
    public static UpdateCommand? Parse(string[] args)
    {
        var apply = Array.IndexOf(args, "--apply-update");
        if (apply < 0 || apply + 1 >= args.Length)
            return null;

        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        return new UpdateCommand(
            args[apply + 1],
            int.TryParse(Value("--pid"), out var pid) ? pid : null,
            Value("--game-dir"),
            int.TryParse(Value("--steam-app-id"), out var appId) ? appId : null,
            args.Contains("--relocated"));
    }

    public string[] ToArgs()
    {
        var args = new List<string> { "--apply-update", ZipPath };
        if (Pid != null) args.AddRange(["--pid", Pid.Value.ToString()]);
        if (GameDir != null) args.AddRange(["--game-dir", GameDir]);
        if (SteamAppId != null) args.AddRange(["--steam-app-id", SteamAppId.Value.ToString()]);
        if (Relocated) args.Add("--relocated");
        return [.. args];
    }
}

public record UpdateOutcome(bool Success, string Message, bool Relaunched = false, ApplyResult? Applied = null);

public static class UpdateRunner
{
    /// <summary>
    /// If running from inside the game folder, copies the updater to a temp folder and starts that copy, because
    /// Windows can't overwrite a running exe (the patch ships the updater itself). Returns true when the caller
    /// must exit and let the copy do the work.
    /// </summary>
    public static bool RelocateIfNeeded(UpdateCommand command, string gameDir, string? processPath = null)
    {
        processPath ??= Environment.ProcessPath;
        if (command.Relocated || processPath == null)
            return false;

        var exeDir = Path.GetDirectoryName(Path.GetFullPath(processPath))!;
        var root = Path.GetFullPath(gameDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!(exeDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        var temp = Path.Combine(Path.GetTempPath(), $"updater-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        foreach (var file in Directory.EnumerateFiles(exeDir))
            File.Copy(file, Path.Combine(temp, Path.GetFileName(file)));

        var psi = new ProcessStartInfo(Path.Combine(temp, Path.GetFileName(processPath))) { UseShellExecute = false };
        foreach (var arg in (command with { GameDir = gameDir, Relocated = true }).ToArgs())
            psi.ArgumentList.Add(arg);
        Process.Start(psi);
        return true;
    }

    /// <summary>Waits for the game to exit, applies the zip, then relaunches through Steam. Never throws.</summary>
    public static async Task<UpdateOutcome> RunAsync(UpdateCommand command, string gameDir, TimeSpan? waitForExit = null)
    {
        try
        {
            if (command.Pid is int pid && !await WaitForExitAsync(pid, waitForExit ?? TimeSpan.FromSeconds(60)))
                return new UpdateOutcome(false, "The game did not close in time; the update was not applied.");

            var applied = PatchApplier.Apply(command.ZipPath, gameDir);
            TryDelete(command.ZipPath);

            var relaunched = command.SteamAppId is int appId && TryRelaunch(appId);
            return new UpdateOutcome(true, $"Updated to {applied.Version}.", relaunched, applied);
        }
        catch (Exception ex)
        {
            return new UpdateOutcome(false, $"Update failed, current install kept: {ex.Message}");
        }
    }

    static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }

        using (process)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    static bool TryRelaunch(int steamAppId)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{steamAppId}") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false; // e.g. inside a Wine prefix: the caller tells the user to start the game
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* leftover staging zip is harmless */ }
    }
}
