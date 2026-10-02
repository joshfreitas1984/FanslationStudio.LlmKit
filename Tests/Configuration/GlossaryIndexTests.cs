using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Tests.Configuration;

public class GlossaryIndexTests
{
    // Reference behaviour: the original per-entry FirstOrDefault + IndexOf merge.
    private static void NaiveMerge(List<GlossaryLine> lines, GlossaryLine newLine)
    {
        var existing = lines.FirstOrDefault(l =>
            (!string.IsNullOrEmpty(newLine.Raw) && l.Raw == newLine.Raw) ||
            (!string.IsNullOrEmpty(newLine.RawSimplified) && l.RawSimplified == newLine.RawSimplified) ||
            (!string.IsNullOrEmpty(newLine.RawTraditional) && l.RawTraditional == newLine.RawTraditional));

        if (existing != null)
            lines[lines.IndexOf(existing)] = newLine;
        else
            lines.Add(newLine);
    }

    [Fact]
    public void ReplaceOrAdd_MatchesNaiveMerge_OnRandomisedOverrides()
    {
        var random = new Random(1234);
        string Key() => random.Next(4) == 0 ? string.Empty : $"k{random.Next(12)}";
        GlossaryLine Line(int id) => new() { Raw = Key(), RawSimplified = Key(), RawTraditional = Key(), Result = $"r{id}" };

        for (var round = 0; round < 200; round++)
        {
            var seed = Enumerable.Range(0, random.Next(0, 15)).Select(Line).ToList();
            var expected = seed.ToList();
            var actual = seed.ToList();
            var index = new ConfigurationExtensions.GlossaryIndex(actual);

            foreach (var newLine in Enumerable.Range(100, random.Next(1, 20)).Select(Line))
            {
                NaiveMerge(expected, newLine);
                index.ReplaceOrAdd(newLine);
            }

            Assert.Equal(expected.Select(l => l.Result), actual.Select(l => l.Result));
        }
    }
}
