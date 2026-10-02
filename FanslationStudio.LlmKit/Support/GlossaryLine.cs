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

        foreach (var line in glossaryLines)
        {
            if (!line.AppliesToFile(outputFile))
                continue;

            string? matched = null;
            if (raw.Contains(line.Raw))
                matched = line.Raw;
            else if (line.RawSimplified != string.Empty && raw.Contains(line.RawSimplified))
                matched = line.RawSimplified;
            else if (line.RawTraditional != string.Empty && raw.Contains(line.RawTraditional))
                matched = line.RawTraditional;

            if (matched == null)
                continue;

            prompt ??= new StringBuilder().AppendLine("```");
            prompt.AppendLine(ToPromptString(matched, line.Result, line.AllowedAlternatives));
        }

        return prompt == null ? string.Empty : prompt.AppendLine("```").ToString();
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
