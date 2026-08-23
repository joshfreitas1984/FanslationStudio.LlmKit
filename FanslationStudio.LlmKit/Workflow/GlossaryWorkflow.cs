using FanslationStudio.LlmKit.Support;
using System.Text;
using ToolGood.Words;


namespace FanslationStudio.LlmKit.Workflow;


public static class GlossaryWorkflow
{
    /// <summary>
    /// Return glossary lines enriched with simplified and traditional Chinese variants if they are missing, based on the original raw text. 
    /// This helps ensure that entries can be matched regardless of the Chinese variant used in the source material.
    /// </summary>
    public static GlossaryLine[] EnrichWithStandardAndTraditionalChinese(GlossaryLine[] lines)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line.RawSimplified) && string.IsNullOrEmpty(line.RawTraditional))
            {
                var traditional = WordsHelper.ToTraditionalChinese(line.Raw);
                var simplified = WordsHelper.ToSimplifiedChinese(line.Raw);                

                if (simplified != line.Raw)
                {
                    line.RawTraditional = line.Raw;
                    line.RawSimplified = simplified;
                }
                else if (traditional != line.Raw)
                {
                    line.RawSimplified = line.Raw;
                    line.RawTraditional = traditional;
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// Returns a report of potential issues in the glossary, including:
    /// Duplicates: Entries that have identical raw text or variants, which can cause confusion and redundancy.
    /// Similar Entries: Entries that have very similar raw text (e.g., differing only in case, whitespace, or minor typos) 
    /// but different translations, which may indicate inconsistencies.
    /// Conflicting Entries: Cases where one entry's raw text contains another entry's raw text (or its variants) marked as a bad translation,
    /// which may indicate potential translation conflicts.
    /// </summary>
    public record GlossaryAnalysisResult(
        HashSet<string> Duplicates,
        HashSet<string> SimilarEntries,
        HashSet<string> ConflictingEntries);

    public static GlossaryAnalysisResult AnalyseGlossaryForIssues(GlossaryLine[] allEntries)
    {
        return new GlossaryAnalysisResult(
            FindDuplicates(allEntries),
            FindSimilarEntries(allEntries),
            FindConflictingEntries(allEntries));
    }

    private static HashSet<string> FindDuplicates(GlossaryLine[] allEntries)
    {
        var seen = new HashSet<string>();
        var duplicates = new HashSet<string>();

        foreach (var entry in allEntries)
            foreach (var variant in GetVariants(entry))
                if (!seen.Add(variant))
                    duplicates.Add(variant);

        return duplicates;
    }

    private static HashSet<string> FindSimilarEntries(GlossaryLine[] allEntries)
    {
        var similar = new HashSet<string>();

        for (int i = 0; i < allEntries.Length; i++)
            for (int j = i + 1; j < allEntries.Length; j++)
            {
                var (e1, e2) = (allEntries[i], allEntries[j]);
                if (AreSimilar(e1.Raw, e2.Raw) && e1.Result != e2.Result)
                    similar.Add($"- raw1: \"{e1.Raw}\" result1: \"{e1.Result}\"\n  raw2: \"{e2.Raw}\" result2: \"{e2.Result}\"");
            }

        return similar;
    }

    private static HashSet<string> FindConflictingEntries(GlossaryLine[] allEntries)
    {
        var conflicts = new HashSet<string>();

        for (int i = 0; i < allEntries.Length; i++)
            for (int j = 0; j < allEntries.Length; j++)
            {
                if (i == j) 
                    continue;

                var baseEntry = allEntries[i];
                var checkingEntry = allEntries[j];

                if (!checkingEntry.CheckForBadTranslation) continue;

                // Check if baseEntry's raw contains any variant of checkingEntry
                var matchedVariant = GetVariants(checkingEntry)
                    .FirstOrDefault(v => baseEntry.Raw != v && baseEntry.Raw.Contains(v));

                // If there's no containment or if it has an allowed translation, skip because its not a conflict
                if (matchedVariant is null || HasCompatibleTranslation(baseEntry, checkingEntry)) 
                    continue;

                conflicts.Add(FormatConflict(baseEntry, checkingEntry, matchedVariant));
            }

        return conflicts;
    }

    private static bool HasCompatibleTranslation(GlossaryLine baseEntry, GlossaryLine checkingEntry) =>
        baseEntry.Result.Contains(checkingEntry.Result, StringComparison.OrdinalIgnoreCase)
        || baseEntry.AllowedAlternatives.Any(a => a.Contains(checkingEntry.Result, StringComparison.OrdinalIgnoreCase))
        || checkingEntry.AllowedAlternatives.Any(a => a.Contains(baseEntry.Result, StringComparison.OrdinalIgnoreCase))
        || checkingEntry.AllowedAlternatives.Any(a => baseEntry.Result.Contains(a, StringComparison.OrdinalIgnoreCase));


    private static IEnumerable<string> GetVariants(GlossaryLine entry)
    {
        var variants = new List<string> { entry.Raw };
        if (!string.IsNullOrEmpty(entry.RawSimplified)) variants.Add(entry.RawSimplified);
        if (!string.IsNullOrEmpty(entry.RawTraditional)) variants.Add(entry.RawTraditional);

        // Ensure an entry never reports a "duplicate" against its own repeated variant
        // (e.g. RawSimplified/RawTraditional falling back to the same text as Raw).
        return variants.Distinct();
    }

    private static string FormatConflict(GlossaryLine baseEntry, GlossaryLine checkingEntry, string matchedVariant)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Containment conflict found:");
        AppendEntry(sb, $"  Containing Entry (has '{matchedVariant}' in '{baseEntry.Raw}'):", baseEntry);
        AppendEntry(sb, "  Contained Entry (badtrans = true):", checkingEntry);
        return sb.ToString();
    }

    private static void AppendEntry(StringBuilder sb, string header, GlossaryLine entry)
    {
        sb.AppendLine(header);
        sb.AppendLine($"    raw: \"{entry.Raw}\"");
        if (!string.IsNullOrEmpty(entry.RawSimplified)) sb.AppendLine($"    rawSimplified: \"{entry.RawSimplified}\"");
        if (!string.IsNullOrEmpty(entry.RawTraditional)) sb.AppendLine($"    rawTraditional: \"{entry.RawTraditional}\"");
        sb.AppendLine($"    result: \"{entry.Result}\"");
        if (entry.AllowedAlternatives.Count > 0)
            sb.AppendLine($"    allowedAlternatives: [{string.Join(", ", entry.AllowedAlternatives.Select(a => $"\"{a}\""))}]");
    }

    private static bool AreSimilar(string str1, string str2)
    {
        if (str1 == str2) return false;
        if (string.Equals(str1, str2, StringComparison.OrdinalIgnoreCase)) return true;
        if (str1.Trim() == str2.Trim()) return true;

        var maxLength = Math.Max(str1.Length, str2.Length);
        return maxLength > 0 && (double)LevenshteinDistance(str1, str2) / maxLength <= 0.2;
    }

    private static int LevenshteinDistance(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1)) return s2?.Length ?? 0;
        if (string.IsNullOrEmpty(s2)) return s1.Length;

        var previousRow = Enumerable.Range(0, s2.Length + 1).ToArray();
        var currentRow = new int[s2.Length + 1];

        for (int i = 1; i <= s1.Length; i++)
        {
            currentRow[0] = i;
            for (int j = 1; j <= s2.Length; j++)
            {
                int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                currentRow[j] = Math.Min(Math.Min(
                    previousRow[j] + 1,
                    currentRow[j - 1] + 1),
                    previousRow[j - 1] + cost);
            }
            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[s2.Length];
    }
}