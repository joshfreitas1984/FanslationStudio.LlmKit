using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// Describes how a single CSV column's Chinese fragments were extracted from a compound cell
/// (e.g. one containing ';', '-', '&amp;', '|' structural separators). The Template holds the
/// original cell text with each translatable fragment replaced by a "{n}" placeholder so the
/// cell can be reconstructed exactly once each fragment has been translated.
/// </summary>
public class FieldTemplate
{
    public int Split { get; set; } = 0;

    /// <summary>
    /// Path/name of the source field this template reconstructs (e.g. a JSON property name,
    /// optionally with an array index suffix like "NameList[2]") - mirrors
    /// <see cref="TranslationSplit.SplitPath"/> and is looked up the same way (exact match, not
    /// stripped of any "[n]" suffix, since each array element is its own independently
    /// reconstructable unit). Empty for every file type that addresses templates by
    /// <see cref="Split"/> instead (CSV columns), which keep using <see cref="Split"/> unchanged.
    /// </summary>
    public string SplitPath { get; set; } = string.Empty;

    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Template { get; set; } = string.Empty;

    /// <summary>
    /// What each named slot (<c>{npcName}</c>) in <see cref="Template"/> stands for, keyed by slot name, for a runtime token that sits
    /// in the template's literal text because no Chinese is next to it (<c>&lt;color=#E64D00&gt;a&lt;/color&gt;{0}</c>). Same meaning as
    /// <see cref="TranslationSplit.Tokens"/>, and the slot names are numbered across the whole cell, so they never clash with a fragment's.
    /// Empty (and not written) when the template has none.
    /// </summary>
    public Dictionary<string, TokenValue> Tokens { get; set; } = new();

    public FieldTemplate() { }

    public FieldTemplate(int split, string template)
    {
        Split = split;
        Template = template;
    }
}
