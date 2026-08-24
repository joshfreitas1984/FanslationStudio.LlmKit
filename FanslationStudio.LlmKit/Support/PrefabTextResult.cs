using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// Final packaged output shape for a <see cref="TextFileType.PrefabText"/> file - a flat list of
/// raw/translated string pairs with no CSV row/column structure to reconstruct, e.g.:
/// <code>
/// - raw: 地图一览
///   result: Map Overview
/// </code>
/// A runtime plugin looks this up by an exact match against <see cref="Raw"/>.
/// </summary>
public class PrefabTextResult
{
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Raw { get; set; } = string.Empty;

    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Result { get; set; } = string.Empty;

    public PrefabTextResult() { }

    public PrefabTextResult(string raw, string result)
    {
        Raw = raw;
        Result = result;
    }
}
