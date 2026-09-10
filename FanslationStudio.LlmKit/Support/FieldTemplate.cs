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

    public FieldTemplate() { }

    public FieldTemplate(int split, string template)
    {
        Split = split;
        Template = template;
    }
}
