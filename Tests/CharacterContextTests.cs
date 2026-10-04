using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;

namespace FanslationStudio.LlmKit.Tests;

public class CharacterContextTests
{
    private static TranslationLine Line(string text) => new() { Raw = text, Splits = [new TranslationSplit { Text = text }] };

    private static string TempFile(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "llmkit-chars-" + Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact(DisplayName = "AddCharacterContext gives a named character's gender, and not over a known speaker or for mixed genders")]
    public void AddCharacterContext_Rules()
    {
        var characters = new Dictionary<string, string> { ["慕容星辰"] = "女", ["空闻大师"] = "male", ["文馨"] = "F" };
        var murong = Line("这慕容星辰号称天下第一神偷。");
        var mixed = Line("慕容星辰与空闻大师同行。");
        var shortName = Line("文馨来了。");
        var unnamed = Line("此人来了。");
        var lines = new List<TranslationLine> { murong, mixed, shortName, unnamed };

        var contexts = new Dictionary<TranslationSplit, LineContext>();
        CharacterContext.AddCharacterContext(contexts, lines, characters, minNameLength: 3);

        var context = contexts[murong.Splits[0]];
        Assert.True(context.GenderKnown);
        Assert.Equal(LineContext.Female, context.Gender);
        Assert.Contains("慕容星辰", context.Prompt);
        // Both genders named: the translator is told who is who, and no single gender is checked against.
        var mixedContext = contexts[mixed.Splits[0]];
        Assert.True(mixedContext.GenderKnown);
        Assert.Equal(string.Empty, mixedContext.Gender);
        Assert.Contains("空闻大师 (male)", mixedContext.Prompt);
        Assert.Contains("慕容星辰 (female)", mixedContext.Prompt);
        // ...so a he or she on that line is neither invented nor contradicting.
        var wrong = new TranslationSplit { Text = mixed.Splits[0].Text, Translated = "Murong Xingchen and Master Kongwen set out, and he led the way." };
        Assert.False(PronounDefectWorkflow.Classify(wrong, null, mixedContext, false).Category is not null);
        Assert.False(contexts.ContainsKey(shortName.Splits[0]));
        Assert.False(contexts.ContainsKey(unnamed.Splits[0]));

        // With the minimum lowered the two-character name matches.
        var all = new Dictionary<TranslationSplit, LineContext>();
        CharacterContext.AddCharacterContext(all, lines, characters);
        Assert.Equal(LineContext.Female, all[shortName.Splits[0]].Gender);

        var known = new Dictionary<TranslationSplit, LineContext> { [murong.Splits[0]] = new LineContext("male", true, LineContext.Male) };
        CharacterContext.AddCharacterContext(known, lines, characters, 3);
        Assert.Equal(LineContext.Male, known[murong.Splits[0]].Gender);

        // A source that already states a gender (大师兄) is left alone: its pronoun may be for that other person.
        var kin = Line("龙湘来到外堡，到处没见着你大师兄。");
        var kinContexts = new Dictionary<TranslationSplit, LineContext>();
        CharacterContext.AddCharacterContext(kinContexts, [kin], new Dictionary<string, string> { ["龙湘"] = "女" });
        Assert.Empty(kinContexts);
        CharacterContext.AddCharacterContext(kinContexts, [kin], new Dictionary<string, string> { ["龙湘"] = "女" }, skipGenderedSources: false);
        Assert.Single(kinContexts);

        var skipped = new Dictionary<TranslationSplit, LineContext>();
        CharacterContext.AddCharacterContext(skipped, lines, characters, 3, split => split.Text.Contains("慕容"));
        Assert.Empty(skipped);
    }

    [Fact(DisplayName = "CharacterContext reads a gender table from YAML (with aliases) and from CSV columns")]
    public void Loaders()
    {
        var yaml = TempFile("c.yaml", "- name: 虞小梅\n  gender: female\n  aliases:\n  - 小梅\n- name: 叶云舟\n  gender: 男\n- name: 无名\n  gender: unknown\n");
        var fromYaml = CharacterContext.FromYaml(yaml);
        Assert.Equal(3, fromYaml.Count);
        Assert.Equal(LineContext.Female, fromYaml["虞小梅"]);
        Assert.Equal(LineContext.Female, fromYaml["小梅"]);
        Assert.Equal(LineContext.Male, fromYaml["叶云舟"]);

        var csv = TempFile("c.csv", "id,名字,性别\n1,慕容.星辰,女\n2,\"空闻,大师\",男\n3,某人,\n");
        var fromCsv = CharacterContext.FromCsv(csv, "名字", "性别", stripFromNames: ".");
        Assert.Equal(2, fromCsv.Count);
        Assert.Equal(LineContext.Female, fromCsv["慕容星辰"]);
        Assert.Equal(LineContext.Male, fromCsv["空闻,大师"]);

        Assert.Empty(CharacterContext.FromYaml("does-not-exist.yaml"));
    }
}
