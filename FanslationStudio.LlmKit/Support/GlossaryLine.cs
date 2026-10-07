using System.Text;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Support;

public class GlossaryLine
{
    public string Raw { get; set; } = string.Empty;
    public string RawSimplified { get; set; } = string.Empty;
    public string RawTraditional { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;


    [YamlMember(Alias = "allowalt")]
    public List<string> AllowedAlternatives { get; set; } = [];
    public string Direct { get; set; } = string.Empty;
    public string Literal { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;

    [YamlMember(Alias = "misuse")]
    public bool CheckForMisusedTranslation { get; set; } = false;
    [YamlMember(Alias = "badtrans")]
    public bool CheckForBadTranslation { get; set; } = true;

    [YamlMember(Alias = "only")]
    public List<string> OnlyOutputFiles { get; set; } = [];

    [YamlMember(Alias = "exclude")]
    public List<string> ExcludeOutputFiles { get; set; } = [];

    public GlossaryLine()
    {
    }

    public GlossaryLine(string raw, string result)
    {
        Raw = raw;
        Result = result;
    }

    /// <summary>
    /// Fenced block of glossary entries whose raw text appears in <paramref name="raw"/>, or
    /// <see cref="string.Empty"/> when nothing matched - callers skip the glossary section entirely
    /// in that case instead of sending an empty block.
    /// </summary>
    public static string AppendPromptsFor(string raw, List<GlossaryLine> glossaryLines, string outputFile)
    {
        StringBuilder? prompt = null;
        var shadowed = FindShadowedByLongerMatch(raw, glossaryLines, outputFile);

        foreach (var line in glossaryLines)
        {
            if (!line.AppliesToFile(outputFile) || shadowed.Contains(line))
                continue;

            var matched = line.MatchIn(raw);
            if (matched == null)
                continue;

            prompt ??= new StringBuilder().AppendLine("```");
            prompt.AppendLine(ToPromptString(matched, line.Result, line.AllowedAlternatives));
        }

        return prompt == null ? string.Empty : prompt.AppendLine("```").ToString();
    }

    /// <summary>
    /// The raw variant (<see cref="Raw"/>, then <see cref="RawSimplified"/>, then
    /// <see cref="RawTraditional"/>) that appears in <paramref name="raw"/>, or null when none does.
    /// </summary>
    public string? MatchIn(string raw)
    {
        if (!string.IsNullOrEmpty(Raw) && raw.Contains(Raw))
            return Raw;
        if (RawSimplified != string.Empty && raw.Contains(RawSimplified))
            return RawSimplified;
        if (RawTraditional != string.Empty && raw.Contains(RawTraditional))
            return RawTraditional;
        return null;
    }

    /// <summary>
    /// Glossary lines whose every occurrence in <paramref name="raw"/> sits inside an occurrence of a
    /// strictly longer glossary term that also matched - e.g. 三七 (the herb, "Sanqi") inside the idiom
    /// 三七开 ("70/30 split"). Matching is otherwise plain substring, so without this the short term
    /// would be injected into the prompt and demanded in the translation although the text is using
    /// the longer term. The one definition shared by the prompt block (<see cref="AppendPromptsFor"/>)
    /// and the rule check that demands a glossary term appear in the translation. A short term that
    /// also occurs on its own elsewhere in <paramref name="raw"/> is not shadowed. Terms only count as
    /// shadowing when they apply to <paramref name="outputFile"/>.
    /// </summary>
    public static HashSet<GlossaryLine> FindShadowedByLongerMatch(string raw, IEnumerable<GlossaryLine> glossaryLines, string outputFile)
    {
        var matches = new List<(GlossaryLine Line, string Matched)>();
        foreach (var line in glossaryLines)
        {
            if (!line.AppliesToFile(outputFile))
                continue;
            var matched = line.MatchIn(raw);
            if (matched != null)
                matches.Add((line, matched));
        }

        var shadowed = new HashSet<GlossaryLine>();
        foreach (var (line, matched) in matches)
        {
            var longer = matches.Where(m => m.Matched.Length > matched.Length && m.Matched.Contains(matched)).ToList();
            if (longer.Count == 0)
                continue;

            var longerSpans = longer.SelectMany(m => OccurrencesOf(raw, m.Matched)).ToList();
            if (OccurrencesOf(raw, matched).All(span => longerSpans.Any(outer => outer.Start <= span.Start && span.End <= outer.End)))
                shadowed.Add(line);
        }

        return shadowed;
    }

    private static IEnumerable<(int Start, int End)> OccurrencesOf(string raw, string term)
    {
        for (var index = raw.IndexOf(term, StringComparison.Ordinal); index >= 0; index = raw.IndexOf(term, index + 1, StringComparison.Ordinal))
            yield return (index, index + term.Length);
    }

    /// <summary>Applies the "only"/"exclude" output-file scoping for this entry.</summary>
    public bool AppliesToFile(string outputFile)
    {
        if (OnlyOutputFiles.Count > 0 && !OnlyOutputFiles.Contains(outputFile))
            return false;

        return ExcludeOutputFiles.Count == 0 || !ExcludeOutputFiles.Contains(outputFile);
    }

    public static string ToPromptString(string raw, string translated, List<string>? alternatives)
    {
        var prompt = new StringBuilder();
        //prompt.AppendLine($"- raw: \"{raw}\"");
        //prompt.AppendLine($"  result: \"{translated}\"");

        //if (alternatives != null)
        //{ 
        //    foreach (var alternative in alternatives)
        //    {
        //        prompt.AppendLine($"- raw: \"{raw}\"");
        //        prompt.AppendLine($"  result: \"{alternative}\"");
        //    }
        //}

        //prompt.AppendLine($"- \"{raw}\": \"{translated}\"");

        prompt.AppendLine($"- raw: \"{raw}\"");
        //prompt.AppendLine($"  result: \"{translated}\"");
        prompt.AppendLine($"  result:");
        prompt.AppendLine($"    - \"{translated}\"");

        //if (alternatives != null)
        //    prompt.AppendLine($"  alternatives: \"{translated}\"");

        foreach (var alternative in alternatives ?? [])
            //prompt.AppendLine($"    - \"{alternative}\"");
            prompt.AppendLine($"    - \"{alternative}\"");

        return prompt.ToString();
    }

    /// <summary>
    /// True if this line is scoped to specific output files ("only") or explicitly excludes some
    /// ("exclude"). Used to keep file-scoped entries out of the run-wide translation cache (see
    /// TranslationService.FillTranslationCacheAsync/TranslateViaLlmAsyncBatched/Pooled) - that
    /// cache is a single flat Raw->Result map shared across every output file, so a translation
    /// tied to one file's context would otherwise silently leak into every other file.
    /// </summary>
    [YamlIgnore]
    public bool IsFileRestricted => OnlyOutputFiles.Count > 0 || ExcludeOutputFiles.Count > 0;
}
