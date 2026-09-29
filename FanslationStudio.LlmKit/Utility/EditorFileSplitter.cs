namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Housekeeping for the in-game UI Editor's rule folders (resizers, layouts, sprites). The editor appends new
/// rules to one auto-created file (zzAddedResizers.yaml, ...); this moves those entries, and any Defaults.yaml,
/// into one file per top-level path prefix so the rules stay reviewable and the auto-created file goes back to
/// empty. Every project runs it while packaging so the released folders look the same across games.
/// </summary>
public static class EditorFileSplitter
{
    public const string DefaultsFile = "Defaults.yaml";
    public const string AddedResizersFile = "zzAddedResizers.yaml";
    public const string AddedLayoutsFile = "zzAddedLayouts.yaml";
    public const string AddedSpritesFile = "zzAddedSprites.yaml";

    /// <summary>File (without extension) for entries whose path has no usable prefix, such as "/*". Sorts last.</summary>
    public const string GlobalFileKey = "zzzGlobalResizer";

    public static void SplitResizers(string workingDirectory) =>
        Split(Path.Combine(workingDirectory, "Resizers"), DefaultsFile, AddedResizersFile);

    public static void SplitLayouts(string workingDirectory) =>
        Split(Path.Combine(workingDirectory, "Layouts"), DefaultsFile, AddedLayoutsFile);

    public static void SplitSprites(string workingDirectory) =>
        Split(Path.Combine(workingDirectory, "Sprites"), DefaultsFile, AddedSpritesFile);

    /// <summary>
    /// Splits the entries of <paramref name="sourceFiles"/> into "&lt;segment0&gt;_&lt;segment1&gt;.yaml" files (first two
    /// path segments, e.g. "Canvas/MainMenu/Title" goes to Canvas_MainMenu.yaml), merging into files that already
    /// exist and sorting by path. Source files are left as empty lists. A missing folder is ignored, so projects
    /// without layouts or sprites can call every method.
    /// </summary>
    public static void Split(string folder, params string[] sourceFiles)
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder))
            return;

        var deserializer = YamlHelper.CreateDeserializer();
        var serializer = YamlHelper.CreateSerializer();

        var groups = new Dictionary<string, List<Dictionary<string, object>>>();

        foreach (var sourceFile in sourceFiles)
        {
            var sourcePath = Path.Combine(folder, sourceFile);
            if (!File.Exists(sourcePath))
                continue;

            var entries = deserializer.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(sourcePath)) ?? [];

            foreach (var entry in entries)
            {
                if (!entry.TryGetValue("path", out var pathValue) || pathValue is not string path)
                    continue;

                var segments = path.Split('/');
                var key = SafeFileName(segments.Length >= 2 ? $"{segments[0]}_{segments[1]}" : segments[0]);

                if (!groups.TryGetValue(key, out var group))
                    groups[key] = group = [];

                group.Add(entry);
            }

            // All entries have been moved out of the source file.
            FileHelper.WriteAllTextWithRetry(sourcePath, serializer.Serialize(new List<Dictionary<string, object>>()));
        }

        foreach (var (key, entries) in groups)
        {
            var outputPath = Path.Combine(folder, $"{key}.yaml");

            var existingEntries = File.Exists(outputPath)
                ? deserializer.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(outputPath)) ?? []
                : [];

            var merged = existingEntries.Concat(entries)
                .OrderBy(entry => entry.TryGetValue("path", out var pathValue) ? pathValue as string : null, StringComparer.Ordinal)
                .ToList();

            FileHelper.WriteAllTextWithRetry(outputPath, serializer.Serialize(merged));
        }
    }

    // Paths can contain wildcards ("MainMap/*/Title", "/*"), which are not legal in file names.
    static string SafeFileName(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(key.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Trim('_').Length == 0 ? GlobalFileKey : safe;
    }
}
