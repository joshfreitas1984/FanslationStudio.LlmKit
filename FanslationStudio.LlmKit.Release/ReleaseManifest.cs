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

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Version { get; set; } = "";
    public List<ReleaseManifestFile> Files { get; set; } = [];

    /// <summary>Manifest paths only written by an installer/updater when missing, so user edits survive updates.</summary>
    public List<string> SeedOnly { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ReleaseManifest FromJson(string json) =>
        JsonSerializer.Deserialize<ReleaseManifest>(json, JsonOptions)
        ?? throw new InvalidDataException("Release manifest is empty.");
}
