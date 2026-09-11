using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Game-specific tuning for <see cref="CompoundFieldSplitter"/>. The shared library has no
/// built-in knowledge of any particular game's placeholder syntax - different games use different
/// tokens (e.g. "#PlayerName#", "{playerName}", "&lt;name&gt;") and some games may even use a
/// character like '#' as a genuine structural separator instead. Each consuming project should
/// build its own <see cref="CompoundFieldSplitterOptions"/> describing the tokens that are safe to
/// glue into surrounding translatable text for its own data, rather than the shared library
/// hardcoding rules for one game.
/// </summary>
public sealed class CompoundFieldSplitterOptions
{
    /// <summary>
    /// Default options with no placeholder patterns configured - behaves exactly like the
    /// original game-agnostic splitting rules (ASCII characters between two Chinese runs remain a
    /// hard fragment boundary).
    /// </summary>
    public static readonly CompoundFieldSplitterOptions Default = new();

    /// <summary>
    /// Regex patterns matching game-specific placeholder tokens (e.g. "#PlayerName#") that must
    /// never act as a fragment boundary. A placeholder's position in the final sentence can
    /// legitimately move during translation (e.g. the name might move to the start or end of the
    /// sentence in the target language), so if it sits between two Chinese runs it gets glued
    /// together with them into a single fragment instead of being left as a fixed literal split
    /// point between two independently-translated fragments.
    /// </summary>
    public IReadOnlyList<Regex> PlaceholderPatterns { get; init; } = [];

    /// <summary>
    /// Extra characters to absorb into a translatable run (i.e. treat as natural sentence text
    /// rather than a fragment boundary), on top of <see cref="CompoundFieldSplitter"/>'s built-in
    /// defaults (CJK punctuation, curly quotes, ellipsis, em dash - see
    /// docs/compoundfieldsplitter-design.md for the full default set and why each one is safe for
    /// any Chinese-source game). Leave empty to keep this game's current, verified behavior, where
    /// plain ASCII punctuation (',', '?', '!', '.', '-', etc.) is always a hard boundary because
    /// this game's data only ever uses it as a structural/game-syntax separator (list items, role
    /// logic, method calls), never as natural Chinese sentence punctuation.
    /// A different game may genuinely use ASCII punctuation as real sentence punctuation instead
    /// (e.g. if its exported text was authored without fullwidth auto-conversion) - in that case,
    /// verify it the same way this default was verified (grep that game's converted/raw text for
    /// the character sitting directly between two Chinese characters) and add it here rather than
    /// changing the shared default, which stays tuned to this game's data.
    /// </summary>
    public IReadOnlyList<char> AdditionalAbsorbedCharacters { get; init; } = [];
}
