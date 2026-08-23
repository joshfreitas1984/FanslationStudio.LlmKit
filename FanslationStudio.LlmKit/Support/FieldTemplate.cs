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

    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Template { get; set; } = string.Empty;

    public FieldTemplate() { }

    public FieldTemplate(int split, string template)
    {
        Split = split;
        Template = template;
    }
}
