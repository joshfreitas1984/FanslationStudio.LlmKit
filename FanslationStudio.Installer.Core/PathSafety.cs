using System.IO.Compression;

namespace FanslationStudio.Installer.Core;

static class PathSafety
{
    /// <summary>Resolves a relative path under root, rejecting rooted paths and '..' escapes (zip-slip).</summary>
    public static string ResolveInside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Unsafe path in release: '{relative}'");

        var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes the game folder: '{relative}'");

        return full;
    }

    public static void ExtractEntry(ZipArchiveEntry entry, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".installing";
        entry.ExtractToFile(temp, true);
        File.Move(temp, destination, true);
    }

    /// <summary>Deletes empty folders from a removed file's folder up to (not including) root.</summary>
    public static void PruneEmptyFolders(string root, string fromFolder)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
        var dir = Path.GetFullPath(fromFolder).TrimEnd('\\', '/');

        while (dir.Length > rootFull.Length
               && dir.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(dir)
               && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir)!;
        }
    }
}
