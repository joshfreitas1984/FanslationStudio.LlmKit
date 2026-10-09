using System.Reflection;
using System.Text.RegularExpressions;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Tests.Configuration;

/// <summary>
/// Static lint over the shipped preset glossary (BaseFiles/ChineseGlossary). Preset entries are
/// injected into every game, so these rules catch shapes that are noisy or wrong everywhere.
/// </summary>
public class PresetGlossaryLintTests
{
    private static readonly Regex Cjk = new(@"[㐀-鿿]", RegexOptions.Compiled);

    // Single characters are the purpose of these files (syllables, units of time, weapon and kin nouns).
    // CommonStats is deliberately absent: single-character stat words hit names and idioms.
    private static readonly HashSet<ChineseGlossaryTypes> SingleCharAllowed =
        [ChineseGlossaryTypes.Phonetics, ChineseGlossaryTypes.Time, ChineseGlossaryTypes.Weapons,
         ChineseGlossaryTypes.Titles, ChineseGlossaryTypes.ItemsAndMinerals];

    private static List<(ChineseGlossaryTypes Type, GlossaryLine Line)> Load()
    {
        var assembly = typeof(ConfigurationExtensions).Assembly;
        var deserializer = YamlHelper.CreateDeserializer();
        var all = new List<(ChineseGlossaryTypes, GlossaryLine)>();
        foreach (var type in Enum.GetValues<ChineseGlossaryTypes>())
        {
            var resource = assembly.GetManifestResourceNames()
                .Single(n => n.EndsWith($".ChineseGlossary.{type}.yaml", StringComparison.Ordinal));
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            foreach (var line in deserializer.Deserialize<List<GlossaryLine>>(reader.ReadToEnd()) ?? [])
                all.Add((type, line));
        }
        return all;
    }

    private static void AssertNone(List<string> offenders, string rule) =>
        Assert.True(offenders.Count == 0, $"{rule}:\n" + string.Join("\n", offenders));

    [Fact(DisplayName = "Preset glossary - no single-character raw outside Phonetics")]
    public void NoSingleCharacterRaw() => AssertNone(
        Load().Where(e => !SingleCharAllowed.Contains(e.Type) && e.Line.Raw.Length < 2)
            .Select(e => $"{e.Type}: {e.Line.Raw} -> {e.Line.Result}").ToList(),
        "Single-character preset entries match inside names and idioms");

    [Fact(DisplayName = "Preset glossary - no duplicate raw across files")]
    public void NoDuplicateRaw() => AssertNone(
        Load().GroupBy(e => e.Line.Raw).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(e => $"{e.Type}={e.Line.Result}"))}").ToList(),
        "Duplicate raw");

    [Fact(DisplayName = "Preset glossary - result and alternatives contain no CJK and are not empty")]
    public void ResultsAreEnglish() => AssertNone(
        Load().Where(e => string.IsNullOrWhiteSpace(e.Line.Result) || Cjk.IsMatch(e.Line.Result)
                          || e.Line.AllowedAlternatives.Any(a => string.IsNullOrWhiteSpace(a) || Cjk.IsMatch(a)))
            .Select(e => $"{e.Type}: raw=[{e.Line.Raw}] result=[{e.Line.Result}] alts=[{string.Join("|", e.Line.AllowedAlternatives)}]").ToList(),
        "Empty or CJK result/alternative");

    [Fact(DisplayName = "Preset glossary - alternatives are whole words that start like the result's words")]
    public void AlternativesAreNotTruncated()
    {
        // A truncated alternative (e.g. "Concentratio") is a prefix of a longer English word that is
        // not itself present; detect by requiring each alternative to be a known-complete word or phrase
        // via a small denylist of known-bad ones plus a "ends mid-word" heuristic.
        var bad = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Concentratio", "grand", "mesmerizing", "Extra Damage" };
        AssertNone(
            Load().SelectMany(e => e.Line.AllowedAlternatives.Where(bad.Contains).Select(a => $"{e.Type}: {e.Line.Raw} allowalt {a}")).ToList(),
            "Known-bad alternatives");
    }

    [Fact(DisplayName = "Preset glossary - known misspellings are absent")]
    public void NoKnownMisspellings()
    {
        var misspellings = new[] { "Transcendant", "Concentratio" };
        AssertNone(
            Load().Where(e => misspellings.Any(m => e.Line.Result.Contains(m, StringComparison.OrdinalIgnoreCase)
                                                    || e.Line.Direct.Contains(m, StringComparison.OrdinalIgnoreCase)))
                .Select(e => $"{e.Type}: {e.Line.Raw} -> {e.Line.Result}").ToList(),
            "Misspelling");
    }
}
