using System.Text.RegularExpressions;

namespace FanslationStudio.Installer.Core;

public static partial class SteamLocator
{
    /// <summary>Best-effort Steam root: Windows registry, then the usual Linux locations.</summary>
    public static string? FindSteamRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var fromRegistry = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (!string.IsNullOrWhiteSpace(fromRegistry) && Directory.Exists(fromRegistry))
                return Path.GetFullPath(fromRegistry);

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var fallback = Path.Combine(programFiles, "Steam");
            return Directory.Exists(fallback) ? fallback : null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { ".steam/steam", ".local/share/Steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam" }
            .Select(p => Path.Combine(home, p))
            .FirstOrDefault(Directory.Exists);
    }

    /// <summary>The Steam root plus every library folder listed in steamapps/libraryfolders.vdf.</summary>
    public static IReadOnlyList<string> LibraryFolders(string steamRoot)
    {
        var folders = new List<string> { steamRoot };

        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            folders.AddRange(ParseLibraryPaths(File.ReadAllText(vdf)));

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IEnumerable<string> ParseLibraryPaths(string vdfText) =>
        PathRegex().Matches(vdfText).Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    /// <summary>Finds steamapps/common/&lt;steamFolderName&gt; in any library.</summary>
    public static string? FindGameFolder(string steamFolderName, IEnumerable<string> libraryFolders) =>
        libraryFolders
            .Select(lib => Path.Combine(lib, "steamapps", "common", steamFolderName))
            .FirstOrDefault(Directory.Exists);

    /// <summary>
    /// Folder that contains the game exe (BepInEx and the patch install beside it). Some games nest the exe
    /// below the Steam folder, so search a few levels down. Null when the exe isn't found.
    /// </summary>
    public static string? FindInstallRoot(string gameFolder, string exeName, int maxDepth = 2)
    {
        var current = new List<string> { gameFolder };
        for (var depth = 0; depth <= maxDepth && current.Count > 0; depth++)
        {
            var hit = current.FirstOrDefault(dir => File.Exists(Path.Combine(dir, exeName)));
            if (hit != null)
                return hit;

            current = current.SelectMany(dir => Directory.EnumerateDirectories(dir)).ToList();
        }

        return null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();
}
