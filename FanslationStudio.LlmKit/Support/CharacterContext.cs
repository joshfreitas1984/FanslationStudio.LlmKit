using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// Tells the translator (and the pronoun check) the gender of a named character, from a table the game points at.
/// A <see cref="Configuration.GameHooks.LineContextProvider"/> loads the table once and calls
/// <see cref="AddCharacterContext"/>: any split that names a character gets "that character is male/female", so a
/// pronoun for them is written and checked against the game's data instead of guessed. A correct pronoun is not
/// flagged, because a known-gender context is checked with <see cref="LineValidation.ContradictsGender"/> (only a
/// pronoun that contradicts the gender), never with the invented-gender rule.
/// </summary>
public static class CharacterContext
{
    /// <summary>One character in a YAML gender table (<see cref="FromYaml"/>).</summary>
    public sealed class Entry
    {
        public string Name { get; set; } = string.Empty;
        /// <summary>male/female (or 男/女, m/f). Anything else means unknown and the entry is ignored.</summary>
        public string Gender { get; set; } = string.Empty;
        /// <summary>Other names the text uses for the same character (a nickname, a given name without the family name).</summary>
        public List<string> Aliases { get; set; } = [];
    }

    /// <summary>Maps male/female/男/女/m/f (any case) to <see cref="LineContext.Male"/>/<see cref="LineContext.Female"/>; null for anything else.</summary>
    public static string? NormaliseGender(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "男" or "male" or "m" or "man" or "boy" => LineContext.Male,
        "女" or "female" or "f" or "woman" or "girl" => LineContext.Female,
        _ => null,
    };

    /// <summary>
    /// Reads names and genders from two columns of a CSV (the header row names them). <paramref name="stripFromNames"/>
    /// removes separator characters stored in the name but absent from the text (a "." between family and given name).
    /// Rows whose gender is not male/female are left out.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FromCsv(string path, string nameColumn, string genderColumn, string stripFromNames = "")
    {
        var characters = new Dictionary<string, string>();
        if (!File.Exists(path))
            return characters;

        var rows = File.ReadAllLines(path);
        if (rows.Length == 0)
            return characters;

        var header = SplitCsv(rows[0].TrimStart('﻿'));
        var nameIndex = Array.IndexOf(header, nameColumn);
        var genderIndex = Array.IndexOf(header, genderColumn);
        if (nameIndex < 0 || genderIndex < 0)
            return characters;

        foreach (var row in rows.Skip(1))
        {
            var columns = SplitCsv(row);
            if (columns.Length <= Math.Max(nameIndex, genderIndex))
                continue;

            var name = stripFromNames.Aggregate(columns[nameIndex], (current, c) => current.Replace(c.ToString(), string.Empty));
            if (name.Length > 0 && NormaliseGender(columns[genderIndex]) is { } gender)
                characters[name] = gender;
        }

        return characters;
    }

    /// <summary>Reads a YAML list of <see cref="Entry"/> (name, gender, aliases). Every name and alias maps to the gender.</summary>
    public static IReadOnlyDictionary<string, string> FromYaml(string path)
    {
        var characters = new Dictionary<string, string>();
        if (!File.Exists(path))
            return characters;

        var entries = YamlHelper.CreateDeserializer().Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [];
        foreach (var entry in entries)
        {
            if (NormaliseGender(entry.Gender) is not { } gender)
                continue;

            foreach (var name in entry.Aliases.Append(entry.Name).Where(name => name.Length > 0))
                characters[name] = gender;
        }

        return characters;
    }

    /// <summary>
    /// Gives each split that names a character from <paramref name="characters"/> (name to male/female/男/女) that
    /// character's gender as a line context. It never replaces a context that already knows a gender (a speaker), and
    /// leaves a split alone when the characters it names have different genders, since the pronoun is then ambiguous.
    /// <paramref name="skipSplit"/> lets a game exclude splits (for example ones with a player token). Names shorter
    /// than <paramref name="minNameLength"/> are not matched, because short names are often ordinary words; list a
    /// short name in the table explicitly only if it is safe, and set the minimum to 1.
    /// </summary>
    public static void AddCharacterContext(
        Dictionary<TranslationSplit, LineContext> contexts,
        IReadOnlyList<TranslationLine> lines,
        IReadOnlyDictionary<string, string> characters,
        int minNameLength = 1,
        Func<TranslationSplit, bool>? skipSplit = null)
    {
        var names = characters
            .Where(character => character.Key.Length >= minNameLength && NormaliseGender(character.Value) != null)
            .Select(character => (Name: character.Key, Gender: NormaliseGender(character.Value)!))
            .ToList();
        if (names.Count == 0)
            return;

        foreach (var split in lines.SelectMany(line => line.Splits))
        {
            if (split.Text.Length == 0 || (contexts.TryGetValue(split, out var existing) && existing.GenderKnown) || skipSplit?.Invoke(split) == true)
                continue;

            var named = names.Where(character => split.Text.Contains(character.Name, StringComparison.Ordinal)).ToList();
            if (named.Count == 0 || named.Select(character => character.Gender).Distinct().Count() != 1)
                continue;

            var male = named[0].Gender == LineContext.Male;
            var who = named.Select(character => character.Name).Distinct().ToList();
            var prompt = $"Context: the text names {string.Join(" and ", who)}, a {(male ? "male" : "female")} character{(who.Count > 1 ? "s" : string.Empty)}. "
                + $"Where a pronoun refers to them, use {(male ? "he/his/him" : "she/her")}. For anyone else, or when the source does not say who is meant, use \"they\" or avoid the pronoun.";
            contexts[split] = new LineContext(prompt, GenderKnown: true, male ? LineContext.Male : LineContext.Female);
        }
    }

    // Minimal CSV line split: quoted fields may contain commas.
    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
                quoted = !quoted;
            else if (c == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(c);
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
