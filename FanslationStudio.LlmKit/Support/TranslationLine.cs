using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Support;

public class TranslationLine
{
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Raw { get; set; } = string.Empty;

    [YamlIgnore]
    public string Translated { get; set; } = string.Empty;

    public List<TranslationSplit> Splits { get; set; } = [];

    /// <summary>
    /// One entry per CSV column that was a compound field (contains multiple Chinese fragments mixed
    /// with structural separators like ';', '-', '&amp;', '|'). Absent/empty for plain columns, which
    /// keep the existing whole-cell replace behavior.
    /// </summary>
    public List<FieldTemplate> Templates { get; set; } = [];

    public TranslationLine() { }

    public TranslationLine(string raw)
    {
        Raw = raw;
    }
}