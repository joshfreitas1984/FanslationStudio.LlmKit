using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Assessments;

/// <summary>One translated unit from a game's Converted data: a source split and its current translation.</summary>
public sealed record CorpusLine(string File, string Source, string Translated);

public sealed class GameEntry
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool Converted { get; set; } = true;
}

public sealed class GameRegistryFile
{
    public List<GameEntry> Games { get; set; } = [];
}

/// <summary>
/// Read-only view of one game: its merged glossary (preset + workspace, loaded exactly as the game
/// loads it) and its Converted lines. Never writes into the game repo.
/// </summary>
public sealed class GameCorpus
{
    public string Name { get; }
    public string FilesPath { get; }
    public List<GlossaryLine> Glossary { get; }
    public IReadOnlyList<CorpusLine> Lines { get; }

    public GameCorpus(string name, string filesPath, List<GlossaryLine> glossary, IReadOnlyList<CorpusLine> lines)
    {
        Name = name;
        FilesPath = filesPath;
        Glossary = glossary;
        Lines = lines;
    }

    /// <summary>Loads every registered game whose checkout exists; the rest are reported through <paramref name="skipped"/>.</summary>
    public static List<GameCorpus> LoadAll(string registryPath, Action<string>? skipped = null)
    {
        var registry = YamlHelper.CreateDeserializer().Deserialize<GameRegistryFile>(File.ReadAllText(registryPath)) ?? new();
        var baseDir = System.IO.Path.GetDirectoryName(registryPath)!;
        var games = new List<GameCorpus>();
        foreach (var entry in registry.Games)
        {
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, entry.Path));
            if (!File.Exists(System.IO.Path.Combine(path, "Config.yaml")))
            {
                skipped?.Invoke($"{entry.Name}: no checkout at {path}");
                continue;
            }

            try
            {
                games.Add(Load(entry.Name, path, entry.Converted));
            }
            catch (Exception ex)
            {
                skipped?.Invoke($"{entry.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return games;
    }

    public static GameCorpus Load(string name, string filesPath, bool includeConverted = true)
    {
        var lines = includeConverted ? ReadConverted(filesPath) : [];
        try
        {
            return new GameCorpus(name, filesPath, ConfigurationExtensions.GetConfiguration(filesPath).Runtime.GlossaryLines, lines);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("At least one model configuration"))
        {
            // A game with no model config (not translated through LlmKit yet) still has a corpus to scan.
            return new GameCorpus(name, filesPath, [], lines);
        }
    }

    private static List<CorpusLine> ReadConverted(string filesPath)
    {
        var directory = System.IO.Path.Combine(filesPath, "Converted");
        var result = new List<CorpusLine>();
        if (!Directory.Exists(directory))
            return result;

        var deserializer = YamlHelper.CreateDeserializer();
        // Converted files are YAML whatever their extension (Xyzj2 keeps .txt names); anything that does not parse is skipped.
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            List<TranslationLine> lines;
            try { lines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(file)) ?? []; }
            catch (YamlDotNet.Core.YamlException) { continue; }
            var name = System.IO.Path.GetFileName(file);
            foreach (var split in lines.SelectMany(l => l.Splits))
                if (!string.IsNullOrWhiteSpace(split.Text) && !string.IsNullOrWhiteSpace(split.Translated))
                    result.Add(new CorpusLine(name, split.Text, split.Translated));
        }
        return result;
    }
}
