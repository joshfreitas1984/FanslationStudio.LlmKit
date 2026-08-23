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
}
