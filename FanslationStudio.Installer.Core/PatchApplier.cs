using System.IO.Compression;
using System.Security.Cryptography;
using FanslationStudio.LlmKit.Release;

namespace FanslationStudio.Installer.Core;

public record ApplyResult(string Version, int Written, int SeedSkipped, int Removed);

/// <summary>Installs, updates and uninstalls a patch zip built by ReleasePackager, driven by its manifest.</summary>
public static class PatchApplier
{
    /// <summary>Where the applied manifest is kept; the in-game updater reads its version from here.</summary>
    public static readonly string InstalledManifestRelativePath = "BepInEx/" + ReleaseManifest.FileName;

    public static ReleaseManifest? LoadInstalled(string installRoot)
    {
        var path = Path.Combine(installRoot, InstalledManifestRelativePath);
        return File.Exists(path) ? ReleaseManifest.FromJson(File.ReadAllText(path)) : null;
    }

    public static string? InstalledVersion(string installRoot) => LoadInstalled(installRoot)?.Version;

    /// <summary>
    /// Verifies every file in the zip against its manifest hash before touching the game folder, so a bad
    /// download changes nothing. Then writes files (skipping existing seed-only ones), removes files the
    /// previous release shipped that this one doesn't, and records the new manifest.
    /// </summary>
    public static ApplyResult Apply(string zipPath, string installRoot)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var manifestEntry = zip.GetEntry(ReleaseManifest.FileName)
                            ?? throw new InvalidDataException($"{ReleaseManifest.FileName} not found in {Path.GetFileName(zipPath)}");
        var manifest = ReleaseManifest.FromJson(ReadText(manifestEntry));
        if (manifest.SchemaVersion > ReleaseManifest.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Release uses manifest schema {manifest.SchemaVersion}; this updater only understands {ReleaseManifest.CurrentSchemaVersion}. Update the installer.");

        var seedOnly = new HashSet<string>(manifest.SeedOnly, StringComparer.OrdinalIgnoreCase);

        // Phase 1: validate everything, write nothing.
        var planned = new List<(ReleaseManifestFile File, ZipArchiveEntry Entry, string Destination)>();
        foreach (var file in manifest.Files)
        {
            var destination = PathSafety.ResolveInside(installRoot, file.Path);
            var entry = zip.GetEntry(file.Path)
                        ?? throw new InvalidDataException($"Release zip is missing {file.Path}");
            if (!HashEquals(entry, file.Sha256))
                throw new InvalidDataException($"Hash mismatch for {file.Path}; the download is corrupt.");
            planned.Add((file, entry, destination));
        }

        var previous = LoadInstalled(installRoot);

        // Phase 2: write.
        int written = 0, skipped = 0;
        foreach (var (file, entry, destination) in planned)
        {
            if (seedOnly.Contains(file.Path) && File.Exists(destination))
            {
                skipped++;
                continue;
            }

            PathSafety.ExtractEntry(entry, destination);
            written++;
        }

        int removed = 0;
        if (previous != null)
        {
            var current = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var previousSeedOnly = new HashSet<string>(previous.SeedOnly, StringComparer.OrdinalIgnoreCase);

            foreach (var old in previous.Files.Where(f => !current.Contains(f.Path) && !previousSeedOnly.Contains(f.Path)))
                removed += DeleteFile(installRoot, old.Path) ? 1 : 0;
        }

        var manifestPath = PathSafety.ResolveInside(installRoot, InstalledManifestRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, manifest.ToJson());

        return new ApplyResult(manifest.Version, written, skipped, removed);
    }

    /// <summary>Removes every file listed in the installed manifest, then the manifest. Returns the count removed.</summary>
    public static int Uninstall(string installRoot, bool keepSeedOnly = false)
    {
        var manifest = LoadInstalled(installRoot);
        if (manifest == null)
            return 0;

        var seedOnly = new HashSet<string>(manifest.SeedOnly, StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in manifest.Files.Where(f => !(keepSeedOnly && seedOnly.Contains(f.Path))))
            removed += DeleteFile(installRoot, file.Path) ? 1 : 0;

        DeleteFile(installRoot, InstalledManifestRelativePath);
        return removed;
    }

    static bool DeleteFile(string installRoot, string relative)
    {
        var path = PathSafety.ResolveInside(installRoot, relative);
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        PathSafety.PruneEmptyFolders(installRoot, Path.GetDirectoryName(path)!);
        return true;
    }

    static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    static bool HashEquals(ZipArchiveEntry entry, string expectedHex)
    {
        using var stream = entry.Open();
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), expectedHex, StringComparison.OrdinalIgnoreCase);
    }
}
