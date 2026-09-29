using System.IO.Compression;
using System.Security.Cryptography;

namespace FanslationStudio.Installer.Core;

/// <summary>Installs the pinned Windows BepInEx build (also used under Wine/Proton on Linux).</summary>
public static class BepInExInstaller
{
    const string MarkerRelativePath = "BepInEx/installer-bepinex.sha256";

    public static bool IsInstalled(BepInExPin pin, string installRoot)
    {
        var marker = Path.Combine(installRoot, MarkerRelativePath);
        return File.Exists(marker)
               && string.Equals(File.ReadAllText(marker).Trim(), pin.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns false when the pinned build is already installed and nothing was downloaded.</summary>
    public static async Task<bool> InstallAsync(InstallerConfig config, string installRoot, IFileDownloader downloader,
        CancellationToken cancellationToken = default)
    {
        config.ValidateBepInExPin();
        var pin = config.BepInEx;

        if (IsInstalled(pin, installRoot))
            return false;

        var temp = Path.Combine(Path.GetTempPath(), $"bepinex-{Guid.NewGuid():N}.zip");
        try
        {
            await downloader.DownloadAsync(pin.Url, temp, cancellationToken);

            await using (var stream = File.OpenRead(temp))
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(actual, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"BepInEx download hash mismatch (expected {pin.Sha256}, got {actual}).");
            }

            using (var zip = ZipFile.OpenRead(temp))
            {
                foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                    PathSafety.ExtractEntry(entry, PathSafety.ResolveInside(installRoot, entry.FullName));
            }

            var marker = Path.Combine(installRoot, MarkerRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, pin.Sha256);
            return true;
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
