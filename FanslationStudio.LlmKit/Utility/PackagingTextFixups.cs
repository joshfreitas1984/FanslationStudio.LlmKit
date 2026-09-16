using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Single call site for every packaging-time text fixup, run from <see cref="Workflow.PrefabTextWorkflow"/>,
/// <see cref="Workflow.DynamicStringWorkflow"/>, <see cref="Workflow.CsvGameDataWorkflow"/>, and
/// <see cref="Workflow.JsonGameDataWorkflow"/> as each translated cell/line is packaged. <see cref="Apply"/>
/// runs the standard, game-agnostic fixups
/// below in order, then <see cref="GameHooks.CustomPackagingFixup"/> if the consuming project has
/// registered one - so a game-specific repair never needs its own duplicate call site in every
/// workflow, only a hook registered once on <see cref="LlmConfig.Hooks"/>.
/// </summary>
public static class PackagingTextFixups
{
    private static readonly Func<string, string, string>[] StandardFixups =
    [
        UndoHyphen,
        FixLiteralNewline,
    ];

    /// <summary>
    /// Runs every standard fixup, then <paramref name="config"/>'s <see cref="GameHooks.CustomPackagingFixup"/>
    /// if one is registered. <paramref name="textFile"/>/<paramref name="column"/> are passed through
    /// to that hook untouched (column is the zero-based CSV column index when known, null for a
    /// plain PrefabText/DynamicString entry).
    /// </summary>
    public static string Apply(LlmConfig config, TextFileToSplit? textFile, int? column, string raw, string result)
    {
        foreach (var fixup in StandardFixups)
            result = fixup(raw, result);

        if (config.Hooks.CustomPackagingFixup != null)
            result = config.Hooks.CustomPackagingFixup(textFile, column, raw, result);

        return result;
    }

    /// <summary>
    /// The LLM/QC pass occasionally "typographically improves" an ASCII hyphen-minus into a Unicode
    /// look-alike non-breaking hyphen (U+2011) instead of leaving it as "-". Undone unconditionally
    /// since a genuine U+2011 in raw source text is not a realistic scenario for translated prose.
    /// </summary>
    private static string UndoHyphen(string raw, string result) => result.Replace("‑", "-");

    /// <summary>
    /// The LLM/QC pass occasionally emits a literal backslash-n ("\n" as two characters) in a
    /// translated result instead of preserving the raw text's own newline convention. Only fixed up
    /// when the raw text never uses a literal "\n" of its own - so raw text that genuinely uses a
    /// literal "\n" (e.g. some prefab text/CSV cells where "\n" is baked into the source as two
    /// characters, replaced by the game's own display code rather than being a real line break) is
    /// left untouched, since that's this field's own encoding and any "\n" the result produces is
    /// presumably intentional too.
    /// </summary>
    private static string FixLiteralNewline(string raw, string result)
    {
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(result))
            return result;

        if (result.Contains("\\n") && !raw.Contains("\\n"))
            return result.Replace("\\n", "\n");

        return result;
    }
}
