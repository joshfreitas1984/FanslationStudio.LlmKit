using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FanslationStudio.Installer.Core;

/// <summary>Installs the pinned Windows BepInEx build (also used under Wine/Proton on Linux).</summary>
public static class BepInExInstaller
{
    const string MarkerRelativePath = "BepInEx/installer-bepinex.sha256";
    const string DoorstopConfigFile = "doorstop_config.ini";

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
        {
            // Applied on every run so an install made before a setting existed gets fixed too.
            ApplyDoorstopSettings(pin, installRoot);
            return false;
        }

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

            ApplyDoorstopSettings(pin, installRoot);

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

    /// <summary>
    /// Writes the pin's dll_search_path_override into doorstop_config.ini. Some Mono games ship their own MonoMod in
    /// Managed, and BepInEx crashes in the preloader unless it loads its own copies from BepInEx\core first.
    /// A missing file or key is left alone.
    /// </summary>
    public static void ApplyDoorstopSettings(BepInExPin pin, string installRoot)
    {
        if (string.IsNullOrWhiteSpace(pin.DllSearchPathOverride))
            return;

        var path = Path.Combine(installRoot, DoorstopConfigFile);
        if (!File.Exists(path))
            return;

        var text = File.ReadAllText(path);
        var updated = Regex.Replace(text, @"(?m)^dll_search_path_override[ \t]*=[^\r\n]*",
            _ => $"dll_search_path_override = \"{pin.DllSearchPathOverride}\"");

        if (updated != text)
            File.WriteAllText(path, updated);
    }
}
