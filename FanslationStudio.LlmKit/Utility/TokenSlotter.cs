using System.Text;
using System.Text.RegularExpressions;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>How a configured token's value is resolved at packaging (see <see cref="NamedTokenReplacer.Kind"/>).</summary>
public enum NamedTokenKind
{
    Copy,
    Fixed,
    Glossary,
    Translate,
}

/// <summary>The outcome of <see cref="TokenSlotter.Restore"/>: the text, and the slots that could not be resolved (left in place).</summary>
public sealed record RestoreResult(string Text, IReadOnlyList<string> Unresolved)
{
    public bool Ok => Unresolved.Count == 0;
}

/// <summary>
/// Turns the runtime tokens a game substitutes into its text (<c>&lt;fName&gt;</c>, a bare <c>a</c>, <c>[大哥||姐姐]</c>) into named
/// slots (<c>{firstName}</c>, <c>{npcName}</c>, <c>{playerTitle}</c>) and back, from <c>Config.yaml: namedTokenReplacers</c>.
/// <see cref="Slot"/> returns the slotted text plus the per-slot values to store with it; <see cref="Restore"/> puts the game's own
/// form back from those values. Both are pure: nothing is remembered between calls, so a restore never depends on what was slotted
/// earlier in the same process.
/// </summary>
public sealed class TokenSlotter
{
    /// <summary>A slotter with nothing configured: every method is a no-op.</summary>
    public static TokenSlotter None { get; } = new(null, []);

    private const int MarkerBase = 0xE100;

    private static readonly Regex NameRegex = new(@"^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);
    private static readonly Regex SlotRegex = new(@"\{\s*([A-Za-z][A-Za-z0-9]*)\s*\}", RegexOptions.Compiled);

    /// <summary>A configured <see cref="NamedTokenReplacer"/> with its regex compiled and its glossary results resolved.</summary>
    private sealed record Entry(NamedTokenReplacer Config, Regex Pattern, IReadOnlyDictionary<string, string> GlossaryResults, NamedTokenKind Kind);

    private static readonly Regex CjkRegex = new(@"[\u3400-\u9fff]", RegexOptions.Compiled);

    private readonly List<Entry> _entries = [];

    /// <summary>
    /// Builds a slotter from the configured tokens (after the glossary is loaded). Throws on a bad entry so a typo fails at start-up
    /// instead of silently sending the raw token to the model.
    /// </summary>
    public TokenSlotter(IEnumerable<NamedTokenReplacer>? configured, IEnumerable<GlossaryLine> glossaryLines)
    {
        var glossary = glossaryLines.ToList();

        foreach (var entry in configured ?? [])
        {
            if (!NameRegex.IsMatch(entry.Name ?? string.Empty))
                throw new InvalidOperationException($"namedTokenReplacers name '{entry.Name}' must be letters and digits, starting with a letter.");

            var hasLiteral = !string.IsNullOrEmpty(entry.Literal);
            if (hasLiteral == !string.IsNullOrEmpty(entry.Pattern))
                throw new InvalidOperationException($"namedTokenReplacers entry '{entry.Name}' needs exactly one of literal or pattern.");

            if (entry.RestoreFromGlossary && !string.IsNullOrEmpty(entry.RestoreAs))
                throw new InvalidOperationException($"namedTokenReplacers entry '{entry.Name}' cannot set both restoreFromGlossary and restoreAs.");

            var kind = ResolveKind(entry);
            var regex = new Regex(hasLiteral ? Regex.Escape(entry.Literal) : entry.Pattern, RegexOptions.Compiled);
            var results = new Dictionary<string, string>();

            // A translate token looks in the glossary first, like a glossary token; it falls back to the model when there is no entry.
            if (kind is NamedTokenKind.Glossary or NamedTokenKind.Translate)
            {
                foreach (var line in glossary.Where(l => !string.IsNullOrEmpty(l.Result)))
                {
                    foreach (var raw in new[] { line.Raw, line.RawSimplified, line.RawTraditional })
                    {
                        var match = string.IsNullOrEmpty(raw) ? null : regex.Match(raw);
                        if (match is { Success: true } && match.Value == raw)
                            results.TryAdd(raw, line.Result);
                    }
                }
            }

            _entries.Add(new Entry(entry, regex, results, kind));
        }
    }

    private static NamedTokenKind ResolveKind(NamedTokenReplacer entry)
    {
        var restoreAs = !string.IsNullOrEmpty(entry.RestoreAs);
        if (string.IsNullOrWhiteSpace(entry.Kind))
            return entry.RestoreFromGlossary ? NamedTokenKind.Glossary : restoreAs ? NamedTokenKind.Fixed : NamedTokenKind.Copy;

        if (!Enum.TryParse<NamedTokenKind>(entry.Kind.Trim(), ignoreCase: true, out var kind))
            throw new InvalidOperationException($"namedTokenReplacers entry '{entry.Name}' has kind '{entry.Kind}'; use copy, fixed, glossary or translate.");

        var problem = kind switch
        {
            NamedTokenKind.Copy when restoreAs || entry.RestoreFromGlossary => "copy takes no restoreAs or restoreFromGlossary",
            NamedTokenKind.Fixed when !restoreAs => "fixed needs restoreAs",
            NamedTokenKind.Fixed when entry.RestoreFromGlossary => "fixed cannot set restoreFromGlossary",
            NamedTokenKind.Glossary when restoreAs => "glossary cannot set restoreAs",
            NamedTokenKind.Translate when restoreAs || entry.RestoreFromGlossary => "translate takes no restoreAs or restoreFromGlossary",
            _ => null,
        };

        return problem == null ? kind : throw new InvalidOperationException($"namedTokenReplacers entry '{entry.Name}': {problem}.");
    }

    public bool HasTokens => _entries.Count > 0;

    /// <summary>
    /// Replaces every configured token in <paramref name="text"/> with its <c>{name}</c> slot. A token that would restore from the
    /// glossary but has no entry stays as written. The same name with the same source reuses its slot; a different source under the
    /// same name becomes <c>{name2}</c>, <c>{name3}</c>. <paramref name="cellTokens"/> shares that numbering across the fragments of
    /// one cell, so a slot name means one thing throughout the cell; the returned map holds only the slots <paramref name="text"/> uses.
    /// </summary>
    public (string Slotted, Dictionary<string, TokenValue> Tokens) Slot(string text, IDictionary<string, TokenValue>? cellTokens = null)
    {
        var used = new Dictionary<string, TokenValue>();
        if (_entries.Count == 0 || string.IsNullOrEmpty(text))
            return (text, used);

        cellTokens ??= used;

        // Each token is masked as one private-use character first: later patterns need the text as written (a code letter next to
        // Chinese, "i1" before "i" can read its digit), and a {name} written straight away would be matched by the patterns after it.
        var slotNames = new List<string>();
        var result = text;
        foreach (var entry in _entries)
        {
            result = entry.Pattern.Replace(result, match =>
            {
                if (!CanSlot(entry, match.Value))
                    return match.Value;

                var name = entry.Config.Name;
                var suffix = 1;
                while (cellTokens.TryGetValue(name, out var taken) && taken.Source != match.Value)
                    name = $"{entry.Config.Name}{++suffix}";

                if (!cellTokens.ContainsKey(name))
                    cellTokens[name] = new TokenValue(match.Value);
                used[name] = cellTokens[name];

                var slot = slotNames.IndexOf(name);
                if (slot < 0)
                {
                    slot = slotNames.Count;
                    slotNames.Add(name);
                }

                return ((char)(MarkerBase + slot)).ToString();
            });
        }

        for (var k = 0; k < slotNames.Count; k++)
            result = result.Replace(((char)(MarkerBase + k)).ToString(), $"{{{slotNames[k]}}}");

        return (result, used);
    }

    /// <summary>
    /// Puts the game's own form back for every slot of <paramref name="tokens"/> in <paramref name="slotted"/> (case- and
    /// whitespace-insensitive, since a model may write <c>{ npcName }</c>). A slot whose value cannot be resolved is left in the text and
    /// listed in <see cref="RestoreResult.Unresolved"/>; a <c>{word}</c> that is not in <paramref name="tokens"/> is not touched.
    /// </summary>
    public RestoreResult Restore(string slotted, IReadOnlyDictionary<string, TokenValue>? tokens)
    {
        if (string.IsNullOrEmpty(slotted) || tokens is not { Count: > 0 })
            return new RestoreResult(slotted, []);

        var byName = new Dictionary<string, KeyValuePair<string, TokenValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in tokens)
            byName.TryAdd(pair.Key, pair);

        var unresolved = new List<string>();
        var lastRestoredEnd = -1;
        var text = SlotRegex.Replace(slotted, match =>
        {
            if (!byName.TryGetValue(match.Groups[1].Value, out var pair))
                return match.Value;

            var value = RestoredValue(pair.Key, pair.Value);
            if (value is null)
            {
                unresolved.Add(pair.Key);
                return match.Value;
            }

            // The game substitutes the value in place, so a model that wrote "{firstName}{playerTitle}" or "{npcName}said" would
            // get "FangBig Brother" in-game. Space the value from an adjacent Latin word or another restored slot (a Chinese
            // neighbour needs none, and a source-form restore of untranslated text keeps its own adjacency).
            var before = match.Index > 0 ? slotted[match.Index - 1] : '\0';
            var afterIndex = match.Index + match.Length;
            var after = afterIndex < slotted.Length ? slotted[afterIndex] : '\0';
            var padBefore = value.Length > 0 && (match.Index == lastRestoredEnd || char.IsAsciiLetterOrDigit(before));
            var padAfter = value.Length > 0 && char.IsAsciiLetterOrDigit(after);
            lastRestoredEnd = afterIndex;

            return (padBefore ? " " : string.Empty) + value + (padAfter ? " " : string.Empty);
        });

        return new RestoreResult(text, unresolved);
    }

    /// <summary>The text a slot restores to, or null when it cannot be resolved (an unknown slot name, or a glossary token with no entry).</summary>
    public string? RestoredValue(string slotName, TokenValue token)
    {
        var entry = EntryFor(slotName, token.Source);
        if (entry == null)
            return null;

        return ValueFor(entry, token.Source)
            ?? (entry.Kind == NamedTokenKind.Translate && token.Translated.Length > 0 ? token.Translated : null);
    }

    /// <summary>True when any configured token is a <c>translate</c> kind, so tokens may need a value translated before packaging.</summary>
    public bool HasTranslatedTokens => _entries.Any(e => e.Kind == NamedTokenKind.Translate);

    /// <summary>
    /// True when <paramref name="token"/> is a <c>translate</c>-kind slot with no glossary entry and no stored translation yet, so
    /// its value has to be translated by the model once.
    /// </summary>
    public bool NeedsModelTranslation(string slotName, TokenValue token)
    {
        if (token.Translated.Length > 0)
            return false;

        var entry = EntryFor(slotName, token.Source);
        return entry is { Kind: NamedTokenKind.Translate } && ValueFor(entry, token.Source) is null;
    }

    /// <summary>
    /// The slots of <paramref name="slottedText"/> whose <see cref="NamedTokenReplacer.ShowValue"/> is on, with the value each stands
    /// for (the translation, for a translate token). A slot with no resolved value yet is left out.
    /// </summary>
    public List<(string Slot, string Value)> ShownValues(string slottedText, IReadOnlyDictionary<string, TokenValue>? tokens)
    {
        var shown = new List<(string, string)>();
        if (_entries.Count == 0 || tokens is not { Count: > 0 })
            return shown;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SlotRegex.Matches(slottedText))
        {
            var name = match.Groups[1].Value;
            if (!seen.Add(name) || !tokens.TryGetValue(name, out var token))
                continue;

            var entry = EntryFor(name, token.Source);
            if (entry is { Config.ShowValue: true } && RestoredValue(name, token) is { Length: > 0 } value)
                shown.Add((name, value));
        }

        return shown;
    }

    /// <summary>The prompt lines giving each shown slot its meaning (see <see cref="ShownValues"/>); empty when none apply.</summary>
    public string DescribeValues(string slottedText, IReadOnlyDictionary<string, TokenValue>? tokens) =>
        string.Join("\n", ShownValues(slottedText, tokens)
            .Select(s => $"`{{{s.Slot}}}` stands for \"{s.Value}\". Keep the slot as written; it is only here so you know what it means."));

    /// <summary>Slots replaced by the source text they stood for: the readable original of a slotted text.</summary>
    public static string RestoreSource(string slotted, IReadOnlyDictionary<string, TokenValue>? tokens)
    {
        if (string.IsNullOrEmpty(slotted) || tokens is not { Count: > 0 })
            return slotted;

        var byName = new Dictionary<string, TokenValue>(tokens, StringComparer.OrdinalIgnoreCase);
        return SlotRegex.Replace(slotted, match => byName.TryGetValue(match.Groups[1].Value, out var token) ? token.Source : match.Value);
    }

    /// <summary>
    /// The reverse of <see cref="Restore"/> for a translation written in restored form (a gold-set or manual translation): each restored
    /// text is put back as its slot, longest text first. Text that holds no restored value is returned as it was.
    /// </summary>
    public string MaskRestored(string restored, IReadOnlyDictionary<string, TokenValue>? tokens)
    {
        if (string.IsNullOrEmpty(restored) || tokens is not { Count: > 0 })
            return restored;

        var result = restored;
        var values = tokens
            .Select(pair => (Name: pair.Key, Text: RestoredValue(pair.Key, pair.Value)))
            .Where(v => !string.IsNullOrEmpty(v.Text))
            .OrderByDescending(v => v.Text!.Length);

        foreach (var (name, text) in values)
            result = result.Replace(text!, $"{{{name}}}");

        return result;
    }

    /// <summary>True when <paramref name="inner"/> (the text between braces) is a configured slot name, with or without a numbering suffix.</summary>
    public bool IsSlotName(string inner)
    {
        foreach (var entry in _entries)
        {
            var name = entry.Config.Name;
            if (inner.Length >= name.Length && inner.StartsWith(name, StringComparison.Ordinal) && IsSuffix(inner.AsSpan(name.Length)))
                return true;
        }

        return false;
    }

    /// <summary>The first configured slot left in <paramref name="text"/> (<c>{npcName}</c>), or null.</summary>
    public string? FindUnrestoredSlot(string text)
    {
        foreach (Match match in SlotRegex.Matches(text))
        {
            if (IsSlotName(match.Groups[1].Value))
                return match.Value;
        }

        return null;
    }

    /// <summary>
    /// The first problem a candidate written in slot space has against its slotted source, as a sentence, or null: a slot the source
    /// has that the candidate has fewer times, a slot the source lacks, or a <c>{word}</c> that is neither a slot nor in the source.
    /// Does nothing when no tokens are configured.
    /// </summary>
    public string? FindSlotProblem(string slottedSource, string slottedCandidate)
    {
        if (_entries.Count == 0)
            return null;

        var needed = CountWords(slottedSource);
        var found = CountWords(slottedCandidate);

        foreach (var (name, count) in needed.Where(n => IsSlotName(n.Key)))
        {
            // allowDroppedRepeats: a repeat may become "you", so one occurrence is enough.
            if (found.GetValueOrDefault(name) < (AllowsDroppedRepeats(name) ? Math.Min(count, 1) : count))
                return $"Missing required token '{{{name}}}'.";
        }

        foreach (var name in found.Keys)
        {
            if (needed.ContainsKey(name))
                continue;

            return IsSlotName(name)
                ? $"Invented slot '{{{name}}}' that the source does not have."
                : $"Unknown placeholder '{{{name}}}' that the source does not have.";
        }

        return null;
    }

    private static Dictionary<string, int> CountWords(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SlotRegex.Matches(text))
            counts[match.Groups[1].Value] = counts.GetValueOrDefault(match.Groups[1].Value) + 1;
        return counts;
    }

    /// <summary>
    /// The prompt lines describing every slot present in <paramref name="slottedText"/>, one per configured
    /// <see cref="NamedTokenReplacer.Description"/>; empty when none apply.
    /// </summary>
    public string Describe(string slottedText)
    {
        var lines = new List<string>();
        foreach (var entry in _entries.Where(e => e.Config.Description.Length > 0))
        {
            if (Regex.IsMatch(slottedText, $@"\{{{entry.Config.Name}\d*\}}") && !lines.Contains(DescriptionLine(entry)))
                lines.Add(DescriptionLine(entry));
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// True when the slot (<c>{playerTitle}</c> or a numbered <c>{playerTitle2}</c>, braces optional) is configured
    /// <see cref="NamedTokenReplacer.AllowDroppedRepeats"/>.
    /// </summary>
    public bool AllowsDroppedRepeats(string slotName)
    {
        var name = slotName.Trim('{', '}').TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return _entries.Any(e => e.Config.AllowDroppedRepeats && e.Config.Name == name);
    }

    private static string DescriptionLine(Entry entry) => $"`{{{entry.Config.Name}}}`: {entry.Config.Description}";

    private Entry? EntryFor(string slotName, string source)
    {
        Entry? first = null;
        foreach (var entry in _entries)
        {
            var name = entry.Config.Name;
            if (!slotName.StartsWith(name, StringComparison.OrdinalIgnoreCase) || !IsSuffix(slotName.AsSpan(name.Length)))
                continue;

            first ??= entry;
            foreach (Match match in entry.Pattern.Matches(source))
            {
                if (match.Value == source)
                    return entry;
            }
        }

        return first;
    }

    // "" (the first slot of a name) or a number from 2 up.
    private static bool IsSuffix(ReadOnlySpan<char> suffix) =>
        suffix.IsEmpty || (suffix.Length <= 3 && int.TryParse(suffix, out var n) && n >= 2 && suffix[0] != '0');

    // What a match restores to without help from the split: null when it needs the glossary and has no entry, or (translate) the model.
    private static string? ValueFor(Entry entry, string matched) => entry.Kind switch
    {
        NamedTokenKind.Copy => matched,
        NamedTokenKind.Fixed => entry.Config.RestoreAs,
        _ => entry.GlossaryResults.TryGetValue(matched, out var result) ? result : null,
    };

    // A glossary token with no entry stays as written; a translate token needs Chinese to translate.
    private static bool CanSlot(Entry entry, string matched) => entry.Kind switch
    {
        NamedTokenKind.Glossary => entry.GlossaryResults.ContainsKey(matched),
        NamedTokenKind.Translate => CjkRegex.IsMatch(matched),
        _ => true,
    };
}
