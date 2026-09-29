namespace FanslationStudio.LlmKit.Release;

/// <summary>Copies <see cref="Source"/> (file or folder) to <see cref="Destination"/> inside the staging folder.</summary>
public record ReleaseMapping(string Source, string Destination);

public class ReleaseOptions
{
    /// <summary>Release version, normally yyyy.MM.dd.HH.mm. Also used as the git tag and release title.</summary>
    public required string Version { get; init; }

    /// <summary>Folder the payload is staged in. Anything else already in it (e.g. PostBuild DLL copies) is kept.</summary>
    public required string StagingFolder { get; init; }

    /// <summary>Where the zip and manifest copy are written. Must not be inside <see cref="StagingFolder"/>.</summary>
    public required string OutputFolder { get; init; }

    /// <summary>Zip name prefix; produces "&lt;prefix&gt;-&lt;version&gt;.zip".</summary>
    public required string ZipPrefix { get; init; }

    public List<ReleaseMapping> Mappings { get; init; } = [];

    /// <summary>Staging-relative folders the project fully owns; deleted before mappings are copied.</summary>
    public List<string> OwnedFolders { get; init; } = [];

    /// <summary>Staging-relative files deleted after mappings are copied (missing files are ignored).</summary>
    public List<string> RemoveAfterStaging { get; init; } = [];

    /// <summary>Markdown written to release-notes.md beside the zip (not inside it). Null writes nothing.</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>
    /// Staging-relative paths or globs ('*' within a folder, '**' across folders) for files the installer/updater
    /// must only write when they don't already exist on the player's machine (e.g. config files), so player edits
    /// survive updates. The packager still ships them; it only records them in the manifest's seedOnly list.
    /// </summary>
    public List<string> SeedOnly { get; init; } = [];
}

public record ReleaseResult(string Version, string ZipPath, string ManifestPath, string? NotesPath, ReleaseManifest Manifest);
