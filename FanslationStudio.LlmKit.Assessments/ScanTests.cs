using System.Text;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Assessments;

public sealed class PresetChange
{
    public string Raw { get; set; } = string.Empty;
    public string OldResult { get; set; } = string.Empty;
}

public sealed class PresetChangesFile
{
    public List<PresetChange> Changes { get; set; } = [];
}

/// <summary>
/// Cross-game scans over the sibling game checkouts in <c>Files/Games.yaml</c>. Read-only; reports land
/// in the gitignored <c>Files/TestResults/Scans/</c>. Manual because they need the sibling repos.
/// </summary>
public class ScanTests
{
    private static string RegistryPath => Path.Combine(AssessmentPaths.WorkingDirectory, "Games.yaml");
    private static string ReportDirectory => Path.Combine(AssessmentPaths.WorkingDirectory, "TestResults", "Scans");

    private static List<GameCorpus> LoadGames()
    {
        var games = GameCorpus.LoadAll(RegistryPath, message => Console.WriteLine("SKIPPED " + message));
        foreach (var game in games)
            Console.WriteLine($"{game.Name}: {game.Glossary.Count} glossary entries, {game.Lines.Count} translated splits");
        return games;
    }

    private static void WriteReport(string name, string content)
    {
        Directory.CreateDirectory(ReportDirectory);
        File.WriteAllText(Path.Combine(ReportDirectory, name), content, new UTF8Encoding(false));
        Console.WriteLine($"Report: {Path.Combine(ReportDirectory, name)}");
    }

    /// <summary>
    /// Workstream E step 2: for every preset entry, per game, how many source splits match it, how often
    /// the translation uses it, and how often it was injected only inside a longer term. Candidates:
    /// dead (never matched), single-game (a move-to-game candidate), contested (high miss rate).
    /// </summary>
    [ManualFact(DisplayName = "3. Scan: preset glossary corpus audit")]
    public void PresetCorpusAudit()
    {
        var games = LoadGames();
        var presetConfig = ConfigurationExtensions.GetConfiguration(AssessmentPaths.WorkingDirectory);
        var presetKeys = presetConfig.Runtime.GlossaryLines.Select(l => l.Raw).ToHashSet();
        var statsByGame = games.ToDictionary(g => g.Name,
            g => GlossaryScans.EntryStats(g.Glossary.Where(l => presetKeys.Contains(l.Raw)), g.Lines).GroupBy(s => s.Raw).ToDictionary(x => x.Key, x => x.First()));

        var report = new StringBuilder("# Preset glossary corpus audit\n\n");
        report.AppendLine($"Games: {string.Join(", ", games.Select(g => $"{g.Name} ({g.Lines.Count} splits)"))}\n");
        report.AppendLine("| Raw | Result | " + string.Join(" | ", games.Select(g => g.Name)) + " | Candidate |");
        report.AppendLine("| --- | --- | " + string.Join(" | ", games.Select(_ => "---")) + " | --- |");

        int dead = 0, single = 0, contested = 0;
        foreach (var entry in presetConfig.Runtime.GlossaryLines.OrderBy(l => l.Raw))
        {
            var cells = new List<string>();
            var matchedIn = 0;
            var totalMatched = 0;
            var totalMiss = 0;
            foreach (var game in games)
            {
                if (!statsByGame[game.Name].TryGetValue(entry.Raw, out var s) || (s.Matched == 0 && s.Shadowed == 0))
                {
                    cells.Add("-");
                    continue;
                }
                if (s.Matched > 0) matchedIn++;
                totalMatched += s.Matched;
                totalMiss += s.Miss;
                cells.Add($"{s.Hit}/{s.Matched}" + (s.Shadowed > 0 ? $" (+{s.Shadowed} shadowed)" : ""));
            }

            var candidate = "";
            if (totalMatched == 0 && games.Any(g => g.Lines.Count > 0)) { candidate = "dead"; dead++; }
            else if (matchedIn == 1 && games.Count(g => g.Lines.Count > 0) > 1) { candidate = "single-game"; single++; }
            else if (totalMatched >= 5 && totalMiss / (double)totalMatched >= 0.5) { candidate = "contested"; contested++; }

            report.AppendLine($"| {entry.Raw} | {entry.Result} | {string.Join(" | ", cells)} | {candidate} |");
        }

        report.AppendLine($"\nCells are `used/matched`. dead={dead}, single-game={single}, contested={contested}.");
        // Evidence for the human review: what the games actually wrote where the preset result was not used.
        report.AppendLine().AppendLine("## Contested entries: sample misses").AppendLine();
        foreach (var entry in presetConfig.Runtime.GlossaryLines.OrderBy(l => l.Raw))
        {
            var stats = games.Select(g => statsByGame[g.Name].GetValueOrDefault(entry.Raw)).Where(s => s != null).ToList();
            var matched = stats.Sum(s => s!.Matched);
            if (matched < 5 || stats.Sum(s => s!.Miss) / (double)matched < 0.5)
                continue;

            report.AppendLine($"### {entry.Raw} -> {entry.Result}").AppendLine();
            foreach (var game in games)
                foreach (var miss in game.Lines.Where(l => l.Source.Contains(entry.Raw) && !GlossaryScans.UsesResult(entry, l.Translated)).Take(3))
                    report.AppendLine($"- {game.Name}: `{miss.Source.ReplaceLineEndings(" ")}` => `{miss.Translated.ReplaceLineEndings(" ")}`");
            report.AppendLine();
        }

        WriteReport("preset-audit.md", report.ToString());
        Assert.NotEmpty(games);
    }

    /// <summary>The preset-change impact scan: per game, how many existing translations use each old result in <c>Files/PresetChanges.yaml</c>.</summary>
    [ManualFact(DisplayName = "4. Scan: preset change impact")]
    public void PresetChangeImpact()
    {
        var games = LoadGames();
        var changes = YamlHelper.CreateDeserializer().Deserialize<PresetChangesFile>(
            File.ReadAllText(Path.Combine(AssessmentPaths.WorkingDirectory, "PresetChanges.yaml"))) ?? new();

        var report = new StringBuilder("# Preset change impact\n\n");
        report.AppendLine("| Raw | Old result | " + string.Join(" | ", games.Select(g => g.Name)) + " |");
        report.AppendLine("| --- | --- | " + string.Join(" | ", games.Select(_ => "---")) + " |");
        foreach (var change in changes.Changes)
        {
            var cells = games.Select(g =>
            {
                var (matches, stale) = GlossaryScans.ChangeImpact(g.Lines, change.Raw, change.OldResult);
                return $"{stale}/{matches}";
            });
            report.AppendLine($"| {change.Raw} | {change.OldResult} | {string.Join(" | ", cells)} |");
        }
        report.AppendLine("\nCells are `translations still using the old result / source splits containing the raw`.");
        WriteReport("preset-change-impact.md", report.ToString());
    }
}
