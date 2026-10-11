using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// What one named slot (<c>{npcName}</c>) in a split's slotted text stands for. Stored per split in
/// <see cref="TranslationSplit.Tokens"/>, keyed by the slot name, and resolved only when the game text is packaged
/// (<see cref="Utility.TokenSlotter.Restore"/>).
/// </summary>
public class TokenValue
{
    /// <summary>The original text the slot replaced (<c>a</c>, <c>&lt;fName&gt;</c>, <c>[大哥||姐姐]</c>).</summary>
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The source translated once, for a <c>translate</c>-kind token with no glossary entry (the glossary, read when the game
    /// text is packaged, wins over this). Empty for every other kind and until the value has been translated.
    /// </summary>
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Translated { get; set; } = string.Empty;

    public TokenValue() { }

    public TokenValue(string source) => Source = source;
}
