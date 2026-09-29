using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanslationStudio.LlmKit.Release;

public class ReleaseManifestFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>
/// Contract read by the installer and the in-game updater. Only add optional fields; bump
/// <see cref="CurrentSchemaVersion"/> for breaking changes.
/// </summary>
public class ReleaseManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "release-manifest.json";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Version { get; set; } = "";

    /// <summary>Release metadata so the in-game updater needs no per-game config. All optional.</summary>
    public string? GitHubRepo { get; set; }
    public int? SteamAppId { get; set; }
    public string? PatchZipPrefix { get; set; }

    public List<ReleaseManifestFile> Files { get; set; } = [];

    /// <summary>Manifest paths only written by an installer/updater when missing, so user edits survive updates.</summary>
    public List<string> SeedOnly { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, ReleaseJsonContext.Default.ReleaseManifest);

    public static ReleaseManifest FromJson(string json) =>
        JsonSerializer.Deserialize(json, ReleaseJsonContext.Default.ReleaseManifest)
        ?? throw new InvalidDataException("Release manifest is empty.");
}

// Source-generated so the manifest reader works in trimmed/AOT-published installers.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReleaseManifest))]
internal partial class ReleaseJsonContext : JsonSerializerContext;
