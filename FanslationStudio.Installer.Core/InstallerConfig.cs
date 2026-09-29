using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanslationStudio.Installer.Core;

public enum BepInExFlavour { Il2Cpp, Mono }

public enum BepInExArchitecture { X64, X86 }

/// <summary>The exact BepInEx build to install. Pinned per game: URL and hash are data, never scraped.</summary>
public class BepInExPin
{
    public BepInExFlavour Flavour { get; set; }
    public BepInExArchitecture Architecture { get; set; }
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

/// <summary>Per-game installer settings (installer.json). Read by the installer, the updater and PackageRelease.</summary>
public class InstallerConfig
{
    public const string FileName = "installer.json";
    public const string DefaultWineLaunchOption = "WINEDLLOVERRIDES=\"winhttp=n,b\" %command%";

    public string GameName { get; set; } = "";
    public int SteamAppId { get; set; }

    /// <summary>Folder under steamapps/common.</summary>
    public string SteamFolderName { get; set; } = "";

    /// <summary>Game exe. Searched a couple of levels below the Steam folder, since some games nest it.</summary>
    public string ExeName { get; set; } = "";

    /// <summary>"owner/repo" that hosts the releases.</summary>
    public string GitHubRepo { get; set; } = "";

    /// <summary>Optional gh account to publish as from PackageRelease. Null means open the browser instead.</summary>
    public string? GhAccount { get; set; }

    /// <summary>Patch zip prefix: releases contain "&lt;prefix&gt;-&lt;version&gt;.zip".</summary>
    public string PatchZipPrefix { get; set; } = "EnglishPatch";

    public string WineLaunchOption { get; set; } = DefaultWineLaunchOption;

    public BepInExPin BepInEx { get; set; } = new();

    public static InstallerConfig Load(string path) => FromJson(File.ReadAllText(path));

    public static InstallerConfig FromJson(string json)
    {
        var config = JsonSerializer.Deserialize(json, InstallerJsonContext.Default.InstallerConfig)
                     ?? throw new InvalidDataException("installer.json is empty.");
        config.Validate();
        return config;
    }

    public string ToJson() => JsonSerializer.Serialize(this, InstallerJsonContext.Default.InstallerConfig);

    /// <summary>Identity fields every consumer needs (packaging, updating, locating the game).</summary>
    public void Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(GameName)) missing.Add(nameof(GameName));
        if (SteamAppId <= 0) missing.Add(nameof(SteamAppId));
        if (string.IsNullOrWhiteSpace(SteamFolderName)) missing.Add(nameof(SteamFolderName));
        if (string.IsNullOrWhiteSpace(ExeName)) missing.Add(nameof(ExeName));
        if (string.IsNullOrWhiteSpace(PatchZipPrefix)) missing.Add(nameof(PatchZipPrefix));
        if (string.IsNullOrWhiteSpace(GitHubRepo) || GitHubRepo.Count(c => c == '/') != 1)
            missing.Add($"{nameof(GitHubRepo)} (owner/repo)");

        if (missing.Count > 0)
            throw new InvalidDataException($"installer.json is missing or invalid: {string.Join(", ", missing)}");
    }

    /// <summary>The BepInEx pin is only needed to install; it may stay blank while only packaging.</summary>
    public void ValidateBepInExPin()
    {
        if (string.IsNullOrWhiteSpace(BepInEx.Url) || string.IsNullOrWhiteSpace(BepInEx.Sha256))
            throw new InvalidDataException("installer.json bepInEx.url and bepInEx.sha256 must be set to install BepInEx.");
    }
}

// Source-generated so installer.json parses in trimmed/AOT-published installers.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstallerConfig))]
internal partial class InstallerJsonContext : JsonSerializerContext;
