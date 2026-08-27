using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// Final packaged output shape for a <see cref="TextFileType.DynamicStringsIL2CPP"/> file - a flat
/// list of raw/translated string-fragment pairs (the hardcoded literal fragments embedded in game
/// code, e.g. "架势"), with no CSV row/column structure to reconstruct, e.g.:
/// <code>
/// - raw: 架势
///   result: Posture
/// </code>
/// A runtime plugin applies this as a global dictionary of exact substring replacements against
/// the return value of every method it patches (see DragonHeirPlugin/DynamicStringPatches.cs) -
/// unlike <see cref="PrefabTextResult"/>'s exact-whole-string lookup, a dynamic string fragment is
/// typically only part of the method's full assembled return value.
/// </summary>
public class DynamicStringResult
{
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Raw { get; set; } = string.Empty;

    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Result { get; set; } = string.Empty;

    public DynamicStringResult() { }

    public DynamicStringResult(string raw, string result)
    {
        Raw = raw;
        Result = result;
    }
}
