using System.Text;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit;

/// <summary>
/// Deterministic last resort for an invented gender: rewrites he/she/his/her/him into singular they/their/them,
/// fixing the verb that follows a subject pronoun (he performs → they perform, she was → they were). Used only after
/// the model has been asked twice to avoid the pronoun and has not. Returns null rather than guess when a pronoun
/// or its verb cannot be rewritten safely, so a wrong rewrite never replaces a flagged line.
/// </summary>
public static partial class PronounRepair
{
    private static readonly HashSet<string> Prepositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "in", "on", "at", "for", "with", "from", "by", "and", "but", "or", "as", "that", "up", "out", "off", "down",
        "away", "back", "if", "when", "while", "so", "than", "into", "onto", "over", "under", "about", "after", "before",
        "of", "through", "toward", "towards", "against", "like", "because", "since", "until", "though", "although",
    };

    // Adverbs that can sit between a subject pronoun and its verb, so the verb is the word after them.
    private static readonly HashSet<string> Adverbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "also", "still", "then", "always", "never", "often", "just", "only", "even", "soon", "now", "already", "really",
        "quickly", "slowly", "suddenly", "finally", "usually", "simply", "merely", "sometimes", "instinctively", "truly",
        "certainly", "definitely", "probably", "perhaps", "rarely", "seldom", "once", "again", "too", "ever", "barely",
    };

    // A word ending in s that is not a third-person verb.
    private static readonly HashSet<string> NotVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "always", "perhaps", "sometimes", "besides", "towards", "afterwards", "this", "thus", "us", "as", "his", "was",
        "is", "has", "does", "less", "unless", "yes", "across", "whereas", "its",
    };

    /// <summary>The rewritten text, or null when nothing needed rewriting or something could not be rewritten safely.</summary>
    public static string? Neutralise(string text)
    {
        var matches = PronounRegex().Matches(text);
        if (matches.Count == 0)
            return null;

        var output = new StringBuilder();
        var position = 0;

        foreach (Match match in matches)
        {
            var word = match.Value;
            var lower = word.ToLowerInvariant();

            // "Chao He": a capital He right after a capitalised word is a name, not the pronoun.
            if (word == "He" && NameBeforeRegex().IsMatch(text[..match.Index]))
                continue;
            var after = text[(match.Index + match.Length)..];
            string? replacement;
            var verbFix = false;

            switch (lower)
            {
                case "he":
                case "she":
                    replacement = "they";
                    verbFix = true;
                    break;
                case "his":
                    replacement = "their";
                    break;
                case "him":
                    replacement = "them";
                    break;
                case "himself":
                case "herself":
                    replacement = "themselves";
                    break;
                case "her":
                    // Possessive when a word follows that is not a preposition/conjunction, otherwise the object "them".
                    var next = NextWord(after);
                    replacement = next == null || Prepositions.Contains(next) || !after.StartsWith(' ') ? "them" : "their";
                    break;
                default:
                    return null;
            }

            replacement = MatchCase(word, replacement);
            output.Append(text, position, match.Index - position).Append(replacement);
            position = match.Index + match.Length;

            if (!verbFix)
                continue;

            // he's / she's / he'd / he'll attach to the pronoun with an apostrophe.
            if (after.StartsWith("'s ", StringComparison.Ordinal) || after.StartsWith("’s ", StringComparison.Ordinal))
            {
                output.Append("'re");
                position += 2;
                continue;
            }

            if (after.StartsWith("'d", StringComparison.Ordinal) || after.StartsWith("'ll", StringComparison.Ordinal))
                continue;

            var fixedVerb = FixVerb(after, out var consumedBefore, out var verbLength, out var verbReplacement);
            if (fixedVerb == VerbFix.Unsafe)
                return null;
            if (fixedVerb == VerbFix.Changed)
            {
                output.Append(text, position, consumedBefore).Append(verbReplacement);
                position += consumedBefore + verbLength;

                // "He washes and goes": a second present-tense verb in the same sentence would also need rewriting, and
                // "tools and arrows" shows a plural noun cannot be told apart from one, so give up rather than guess.
                if (ChainedPresentVerbRegex().IsMatch(SentenceRest(text, position)))
                    return null;
            }
        }

        output.Append(text, position, text.Length - position);
        var result = output.ToString();
        return result == text ? null : result;
    }

    private enum VerbFix { Unchanged, Changed, Unsafe }

    private static VerbFix FixVerb(string after, out int consumedBefore, out int verbLength, out string replacement)
    {
        consumedBefore = 0;
        verbLength = 0;
        replacement = string.Empty;

        var offset = 0;
        while (true)
        {
            var match = LeadingWordRegex().Match(after, offset);
            if (!match.Success)
                return VerbFix.Unchanged;

            var word = match.Groups[1].Value;
            if (Adverbs.Contains(word))
            {
                offset = match.Index + match.Length;
                continue;
            }

            consumedBefore = match.Groups[1].Index;
            verbLength = word.Length;
            var lower = word.ToLowerInvariant();

            var special = lower switch
            {
                "was" => "were",
                "is" => "are",
                "has" => "have",
                "does" => "do",
                "wasn't" => "weren't",
                "isn't" => "aren't",
                "hasn't" => "haven't",
                "doesn't" => "don't",
                _ => null,
            };
            if (special != null)
            {
                replacement = special;
                return VerbFix.Changed;
            }

            if (NotVerbs.Contains(lower) || !lower.EndsWith('s') || lower.EndsWith("ss", StringComparison.Ordinal) || lower.Length < 3)
                return VerbFix.Unchanged;

            // A present-tense third-person verb. Anything after "he" that ends in s and is not a known non-verb is
            // treated as one; the rare exception (a plural noun directly after the pronoun) is not valid English.
            replacement = lower switch
            {
                _ when lower.EndsWith("ies", StringComparison.Ordinal) && lower.Length > 4 => lower[..^3] + "y",
                _ when Regex.IsMatch(lower, "(ch|sh|x|z|o)es$") => lower[..^2],
                "goes" => "go",
                _ => lower[..^1],
            };
            return VerbFix.Changed;
        }
    }

    private static string SentenceRest(string text, int from)
    {
        var end = text.IndexOfAny(['.', '!', '?'], from);
        return end < 0 ? text[from..] : text[from..end];
    }

    private static string? NextWord(string after)
    {
        var match = LeadingWordRegex().Match(after);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string MatchCase(string original, string replacement) =>
        original.Length > 0 && char.IsUpper(original[0]) ? char.ToUpperInvariant(replacement[0]) + replacement[1..] : replacement;

    [GeneratedRegex(@"\b(?:he|she|his|her|him|himself|herself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PronounRegex();

    [GeneratedRegex(@"[A-Z][a-z]+ $")]
    private static partial Regex NameBeforeRegex();

    [GeneratedRegex(@"\b(?:and|but|or)\s+(?:(?:also|still|then|never|always|just|only|even|often)\s+)?(?!(?:his|this|was|its|as|us|yes)\b)[A-Za-z]+s\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChainedPresentVerbRegex();

    [GeneratedRegex(@"\G\s*([A-Za-z']+)")]
    private static partial Regex LeadingWordRegex();
}
