using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Assessments;

/// <summary>Per-entry corpus statistics for one game.</summary>
public sealed record EntryStat(string Raw, string Result, int Matched, int Hit, int Shadowed)
{
    public int Miss => Matched - Hit;
    public double MissRate => Matched == 0 ? 0 : Miss / (double)Matched;
}

/// <summary>
/// Deterministic scans over a corpus and a glossary (pure functions, no LLM): how often each entry
/// matches, how often the translation actually uses it, and how often it is injected only inside a
/// longer term. Matching mirrors production: <see cref="GlossaryLine.SelectFor"/> with the shadowing
/// rule, then a case-insensitive check for the result or an allowed alternative in the translation.
/// </summary>
public static class GlossaryScans
{
    public static List<EntryStat> EntryStats(IEnumerable<GlossaryLine> glossary, IReadOnlyList<CorpusLine> lines)
    {
        var entries = glossary.ToList();
        var matched = new int[entries.Count];
        var hit = new int[entries.Count];
        var shadowed = new int[entries.Count];
        var comparer = ReferenceEqualityComparer.Instance as IEqualityComparer<GlossaryLine>;
        var index = new Dictionary<GlossaryLine, int>(comparer);
        for (var i = 0; i < entries.Count; i++)
            index[entries[i]] = i;

        foreach (var line in lines)
        {
            var applicable = entries.Where(e => e.MatchIn(line.Source) != null).ToList();
            if (applicable.Count == 0)
                continue;
            var live = GlossaryLine.SelectFor(line.Source, applicable, string.Empty).ToHashSet(comparer);

            foreach (var entry in applicable)
            {
                var i = index[entry];
                if (!live.Contains(entry))
                {
                    shadowed[i]++;
                    continue;
                }
                matched[i]++;
                if (UsesResult(entry, line.Translated))
                    hit[i]++;
            }
        }

        return entries.Select((e, i) => new EntryStat(e.Raw, e.Result, matched[i], hit[i], shadowed[i])).ToList();
    }

    public static bool UsesResult(GlossaryLine entry, string translated) =>
        translated.Contains(entry.Result, StringComparison.OrdinalIgnoreCase)
        || entry.AllowedAlternatives.Any(a => translated.Contains(a, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Preset-change impact: lines whose source contains <paramref name="raw"/> and whose translation
    /// contains <paramref name="oldResult"/> - what a changed entry would leave stale.
    /// </summary>
    public static (int SourceMatches, int UsingOldResult) ChangeImpact(IReadOnlyList<CorpusLine> lines, string raw, string oldResult)
    {
        var matches = lines.Where(l => l.Source.Contains(raw)).ToList();
        return (matches.Count, matches.Count(l => l.Translated.Contains(oldResult, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Detector blast radius: the lines a new detector would flag.</summary>
    public static List<CorpusLine> Flagged(IReadOnlyList<CorpusLine> lines, Func<CorpusLine, bool> detector) =>
        lines.Where(detector).ToList();
}
