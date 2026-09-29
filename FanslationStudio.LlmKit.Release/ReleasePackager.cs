using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Release;

public static class ReleasePackager
{
    public static ReleaseResult Package(ReleaseOptions options)
    {
        var staging = Path.GetFullPath(options.StagingFolder);
        var output = Path.GetFullPath(options.OutputFolder);

        if (IsInside(staging, output))
            throw new ArgumentException("OutputFolder must not be inside StagingFolder.");

        // Validate everything up front so a bad path cannot half-clear the staging folder.
        var owned = options.OwnedFolders.Select(f => ResolveInside(staging, f)).ToList();
        var mappings = options.Mappings
            .Select(m => (Source: Path.GetFullPath(m.Source), Destination: ResolveInside(staging, m.Destination)))
            .ToList();
        var removals = options.RemoveAfterStaging.Select(f => ResolveInside(staging, f)).ToList();

        foreach (var (source, _) in mappings)
        {
            if (!File.Exists(source) && !Directory.Exists(source))
                throw new FileNotFoundException($"Release mapping source does not exist: {source}");
        }

        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(output);

        foreach (var folder in owned)
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }

        foreach (var (source, destination) in mappings)
        {
            if (Directory.Exists(source))
                CopyDirectory(source, destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, true);
            }
        }

        foreach (var file in removals)
            File.Delete(file);

        var manifestInStaging = Path.Combine(staging, ReleaseManifest.FileName);
        File.Delete(manifestInStaging);

        var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(staging, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(rel => new ReleaseManifestFile
            {
                Path = rel,
                Sha256 = HashFile(Path.Combine(staging, rel)),
                Size = new FileInfo(Path.Combine(staging, rel)).Length,
            })
            .ToList();

        var manifest = new ReleaseManifest
        {
            Version = options.Version,
            Files = files,
            SeedOnly = ResolveSeedOnly(options.SeedOnly, files),
        };

        File.WriteAllText(manifestInStaging, manifest.ToJson());

        var zipPath = Path.Combine(output, $"{options.ZipPrefix}-{options.Version}.zip");
        File.Delete(zipPath);
        ZipFile.CreateFromDirectory(staging, zipPath);

        var manifestPath = Path.Combine(output, ReleaseManifest.FileName);
        File.Copy(manifestInStaging, manifestPath, true);

        string? notesPath = null;
        if (!string.IsNullOrWhiteSpace(options.ReleaseNotes))
        {
            notesPath = Path.Combine(output, ReleasePublisher.NotesFileName);
            File.WriteAllText(notesPath, options.ReleaseNotes);
        }

        return new ReleaseResult(options.Version, zipPath, manifestPath, notesPath, manifest);
    }

    static List<string> ResolveSeedOnly(IEnumerable<string> patterns, List<ReleaseManifestFile> files)
    {
        var result = new List<string>();
        foreach (var pattern in patterns)
        {
            var regex = GlobToRegex(pattern);
            var matches = files.Where(f => regex.IsMatch(f.Path)).Select(f => f.Path).ToList();
            if (matches.Count == 0)
                throw new InvalidOperationException($"SeedOnly pattern matched no staged file: {pattern}");
            result.AddRange(matches);
        }

        return result.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    static Regex GlobToRegex(string glob)
    {
        var escaped = Regex.Escape(glob.Replace('\\', '/'))
            .Replace(@"\*\*/", "(.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase);
    }

    static string ResolveInside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new ArgumentException($"Path must be relative to the staging folder: '{relative}'");

        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsInside(root, full) || SamePath(full, root))
            throw new ArgumentException($"Path escapes (or is) the staging folder: '{relative}'");

        return full;
    }

    static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static bool IsInside(string parent, string child)
    {
        var p = parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var c = child.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
