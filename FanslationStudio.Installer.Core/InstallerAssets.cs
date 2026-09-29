namespace FanslationStudio.Installer.Core;

/// <summary>
/// The installer binaries live in one rolling pre-release tagged "installer", separate from the patch releases.
/// A pre-release never counts as "latest", so patch checks are unaffected, and the download URL never changes,
/// so the installer is only rebuilt and re-uploaded when its own code or installer.json changes.
/// </summary>
public static class InstallerAssets
{
    public const string ReleaseTag = "installer";
    public const string WindowsFileName = "Installer-win-x64.exe";
    public const string LinuxFileName = "Installer-linux-x64";

    /// <summary>Stable download link, e.g. for the in-game updater. Wine users need the Windows build.</summary>
    public static string DownloadUrl(string ownerAndRepo, bool windows) =>
        $"https://github.com/{ownerAndRepo}/releases/download/{ReleaseTag}/{(windows ? WindowsFileName : LinuxFileName)}";

    /// <summary>Prefilled "new release" page for the rolling installer pre-release.</summary>
    public static string NewReleaseUrl(string ownerAndRepo) =>
        $"https://github.com/{ownerAndRepo}/releases/new?tag={ReleaseTag}&title=Installer&prerelease=1";

    /// <summary>Edit page of the existing rolling pre-release, where the two binaries are replaced.</summary>
    public static string EditReleaseUrl(string ownerAndRepo) =>
        $"https://github.com/{ownerAndRepo}/releases/edit/{ReleaseTag}";
}
