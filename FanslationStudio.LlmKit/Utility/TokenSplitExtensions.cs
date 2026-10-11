using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// A split's text in the form the game has it. <see cref="TranslationSplit.Text"/> and <see cref="TranslationSplit.Translated"/> hold
/// named slots (<c>{npcName}</c>) when <c>namedTokenReplacers</c> is configured; a game's own hooks and the pronoun checks look for the
/// game's tokens (<c>///a///</c>, <c>&lt;fName&gt;</c>), so they are handed these. Identical to the stored text when the split has no tokens.
/// </summary>
internal static class TokenSplitExtensions
{
    public static string TextInGameForm(this TranslationSplit split) => InGameForm(split.Text, split.Tokens);

    public static string TranslatedInGameForm(this TranslationSplit split) => InGameForm(split.Translated, split.Tokens);

    /// <summary>The text as it was before slotting (each slot replaced by its source), the key a manual translation is looked up by.</summary>
    public static string OriginalText(this TranslationSplit split) => TokenSlotter.RestoreSource(split.Text, split.Tokens);

    public static string InGameForm(string slotted, IReadOnlyDictionary<string, TokenValue>? tokens) =>
        tokens is { Count: > 0 } ? StringTokenReplacer.Slotter.Restore(slotted, tokens).Text : slotted;
}
