using System.Reflection;

namespace FanslationStudio.Installer.Core;

public record InstallSummary(string Version, bool BepInExInstalled, bool AlreadyUpToDate, ApplyResult? Applied);

/// <summary>The install/update/uninstall sequence the GUI drives. UI-free so it can be tested.</summary>
public static class InstallerService
{
    /// <summary>Installs the pinned BepInEx if needed, then the latest patch release if newer than what's installed.</summary>
    public static async Task<InstallSummary> InstallOrUpdateAsync(InstallerConfig config, string installRoot, HttpClient http,
        IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        var downloader = new HttpFileDownloader(http);

        progress.Report("Checking BepInEx...");
        var bepInExInstalled = await BepInExInstaller.InstallAsync(config, installRoot, downloader, cancellationToken);
        progress.Report(bepInExInstalled ? "BepInEx installed." : "BepInEx is already installed.");

        progress.Report("Looking for the latest patch release...");
        var latest = await ReleaseClient.GetLatestAsync(http, config.GitHubRepo, config.PatchZipPrefix, cancellationToken)
                     ?? throw new InvalidOperationException($"No releases have been published for {config.GitHubRepo} yet.");

        var installed = PatchApplier.InstalledVersion(installRoot);
        if (!ReleaseClient.IsNewer(installed, latest.Version))
        {
            progress.Report($"Patch {installed} is already up to date.");
            return new InstallSummary(installed!, bepInExInstalled, true, null);
        }

        var zip = Path.Combine(Path.GetTempPath(), latest.ZipName);
        try
        {
            progress.Report($"Downloading {latest.ZipName}...");
            await downloader.DownloadAsync(latest.ZipUrl, zip, cancellationToken);

            progress.Report("Applying patch...");
            var applied = PatchApplier.Apply(zip, installRoot);
            progress.Report($"Patch {applied.Version} applied ({applied.Written} files written, {applied.SeedOnlySkippedText()}).");
            return new InstallSummary(applied.Version, bepInExInstalled, false, applied);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    /// <summary>Removes the patch files listed in the installed manifest. BepInEx itself is left in place.</summary>
    public static int Uninstall(string installRoot, IProgress<string> progress)
    {
        var removed = PatchApplier.Uninstall(installRoot);
        progress.Report(removed == 0 ? "No installed patch was found." : $"Removed {removed} patch files. BepInEx was left in place.");
        return removed;
    }

    /// <summary>Resolves the game's install root from Steam. Null when Steam or the game isn't found.</summary>
    public static string? FindInstallRoot(InstallerConfig config)
    {
        var steam = SteamLocator.FindSteamRoot();
        if (steam == null)
            return null;

        var gameFolder = SteamLocator.FindGameFolder(config.SteamFolderName, SteamLocator.LibraryFolders(steam));
        return gameFolder == null ? null : SteamLocator.FindInstallRoot(gameFolder, config.ExeName);
    }

    /// <summary>
    /// Config next to the exe (for testing another repo or game) overrides the installer.json embedded in the
    /// entry assembly.
    /// </summary>
    public static InstallerConfig LoadConfig(Assembly hostAssembly, string exeFolder)
    {
        var overridePath = Path.Combine(exeFolder, InstallerConfig.FileName);
        if (File.Exists(overridePath))
            return InstallerConfig.Load(overridePath);

        var resource = hostAssembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(InstallerConfig.FileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("installer.json is not embedded in this build and none was found next to the exe.");

        using var reader = new StreamReader(hostAssembly.GetManifestResourceStream(resource)!);
        return InstallerConfig.FromJson(reader.ReadToEnd());
    }

    static string SeedOnlySkippedText(this ApplyResult applied) =>
        applied.SeedSkipped == 0 ? "no settings files kept" : $"{applied.SeedSkipped} existing settings files kept";
}
