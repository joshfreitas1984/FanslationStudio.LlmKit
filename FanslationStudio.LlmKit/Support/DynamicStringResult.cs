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

    /// <summary>
    /// True when <see cref="Raw"/> is (or contains) a <c>String.Format</c>-style template - i.e.
    /// still has literal <c>{0}</c>/<c>{1}</c>/... placeholders, e.g. <c>"{0}年{1}月{2}日"</c> -
    /// rather than a plain literal fragment. A runtime plugin must apply these entries to the
    /// *template argument itself* before the game's own <c>String.Format</c> call substitutes
    /// real data into the placeholders (see <c>DynamicStringPatches.FormatPrefix</c>), since a
    /// template's literal placeholder text can never appear as a substring of the *already*
    /// formatted result - the placeholders are gone by then. Computed once here at packaging time
    /// (rather than re-derived by every runtime consumer via ad-hoc string sniffing, e.g.
    /// <c>Raw.Contains('{')</c>) so the flag is an explicit, inspectable part of the packaged
    /// contract instead of an implicit convention duplicated across consumers.
    /// </summary>
    public bool IsTemplate { get; set; }

    public DynamicStringResult() { }

    public DynamicStringResult(string raw, string result, bool isTemplate = false)
    {
        Raw = raw;
        Result = result;
        IsTemplate = isTemplate;
    }
}
