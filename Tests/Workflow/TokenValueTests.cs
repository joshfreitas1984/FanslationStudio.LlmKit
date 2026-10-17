using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using FanslationStudio.LlmKit.Workflow;

namespace FanslationStudio.LlmKit.Tests.Workflow;

/// <summary>
/// Token kinds and phrase tokens: a <c>translate</c> token's value is the glossary Result for its source (read when the game text is
/// packaged) or the source translated once by the model and stored with the split; a phrase repeated across lines is one model call,
/// and <c>showValue</c> tells the model what a slot stands for without letting it rewrite it.
/// </summary>
[Collection("StringTokenReplacerStatics")]
public class TokenValueTests : IDisposable
{
    private const string Farewell = "后会有期";

    private static NamedTokenReplacer Phrase(bool showValue = true) =>
        new() { Literal = Farewell, Name = "farewell", Kind = "translate", ShowValue = showValue, Description = "a fixed parting phrase." };

    private readonly string _dir = Directory.CreateTempSubdirectory("llmkit-token-values-").FullName;

    public void Dispose()
    {
        StringTokenReplacer.SetNamedTokens([], []);
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static TokenSlotter Slotter(IEnumerable<GlossaryLine>? glossary = null, params NamedTokenReplacer[] entries) =>
        new(entries.Length > 0 ? entries : [Phrase()], glossary ?? []);

    private static Dictionary<string, TokenValue> Tokens(string source, string translated = "") =>
        new() { ["farewell"] = new TokenValue(source) { Translated = translated } };

    // ---------------------------------------------------------------- config

    [Theory(DisplayName = "An explicit kind must agree with restoreAs and restoreFromGlossary")]
    [InlineData("copy", "///x///", false)]
    [InlineData("fixed", "", false)]
    [InlineData("fixed", "///x///", true)]
    [InlineData("glossary", "///x///", false)]
    [InlineData("translate", "///x///", false)]
    [InlineData("translate", "", true)]
    [InlineData("copy", "", true)]
    [InlineData("bogus", "", false)]
    public void Kind_IsValidated(string kind, string restoreAs, bool valid)
    {
        var create = () => new TokenSlotter([new NamedTokenReplacer { Literal = "<x>", Name = "x", Kind = kind, RestoreAs = restoreAs }], []);

        if (valid)
            create();
        else
            Assert.Throws<InvalidOperationException>(create);
    }

    [Fact(DisplayName = "restoreAs and restoreFromGlossary remain aliases for fixed and glossary")]
    public void Aliases_StillWork()
    {
        var slotter = new TokenSlotter(
        [
            new NamedTokenReplacer { Literal = "<a>", Name = "a", RestoreAs = "///a///" },
            new NamedTokenReplacer { Literal = "<b>", Name = "b", Kind = "fixed", RestoreAs = "///b///" },
            new NamedTokenReplacer { Literal = "<c>", Name = "c", Kind = "copy" },
        ], []);
        var (slotted, tokens) = slotter.Slot("<a><b><c>");

        Assert.Equal("///a/// ///b/// <c>", slotter.Restore(slotted, tokens).Text);
    }

    [Fact(DisplayName = "A restored token is spaced from an adjacent Latin word or restored token, not from punctuation or Chinese")]
    public void Restore_SpacesAdjacentTokens()
    {
        var slotter = new TokenSlotter(
        [
            new NamedTokenReplacer { Literal = "<fName>", Name = "firstName" },
            new NamedTokenReplacer { Literal = "<t>", Name = "playerTitle", RestoreAs = "<Big Brother||Big Sister>" },
        ], []);
        var (_, tokens) = slotter.Slot("<fName><t>");

        Assert.Equal("<fName> <Big Brother||Big Sister>, hi", slotter.Restore("{firstName}{playerTitle}, hi", tokens).Text);
        Assert.Equal("Hi <fName>, <Big Brother||Big Sister>.", slotter.Restore("Hi {firstName}, {playerTitle}.", tokens).Text);
        Assert.Equal("Hi <fName> and", slotter.Restore("Hi{firstName}and", tokens).Text);
        Assert.Equal("你<fName>好", slotter.Restore("你{firstName}好", tokens).Text);
    }

    // ---------------------------------------------------------------- slotting and restoring

    [Fact(DisplayName = "A translate token is slotted, with no value until it is translated")]
    public void Translate_IsSlotted()
    {
        var (slotted, tokens) = Slotter().Slot($"{Farewell}，朋友");

        Assert.Equal("{farewell}，朋友", slotted);
        Assert.Equal(Farewell, tokens["farewell"].Source);
        Assert.Equal(string.Empty, tokens["farewell"].Translated);
    }

    [Fact(DisplayName = "A translate token needs Chinese to translate")]
    public void Translate_NeedsChinese()
    {
        var slotter = new TokenSlotter([new NamedTokenReplacer { Literal = "ok", Name = "ok", Kind = "translate" }], []);

        Assert.Equal("ok", slotter.Slot("ok").Slotted);
    }

    [Fact(DisplayName = "A translate token restores to its stored translation")]
    public void Translate_RestoresStoredValue()
    {
        var result = Slotter().Restore("{farewell}!", Tokens(Farewell, "See you again"));

        Assert.Equal("See you again!", result.Text);
    }

    [Fact(DisplayName = "The glossary, as it is when restoring, wins over the stored translation")]
    public void Translate_GlossaryWins()
    {
        var slotter = Slotter([new GlossaryLine(Farewell, "Until we meet")]);

        Assert.Equal("Until we meet!", slotter.Restore("{farewell}!", Tokens(Farewell, "See you again")).Text);
        Assert.Equal("Until we meet!", slotter.Restore("{farewell}!", Tokens(Farewell)).Text);
    }

    [Fact(DisplayName = "A translate token with neither a glossary entry nor a translation cannot be restored")]
    public void Translate_WithoutValue_IsUnresolved()
    {
        var result = Slotter().Restore("{farewell}!", Tokens(Farewell));

        Assert.False(result.Ok);
        Assert.Equal(["farewell"], result.Unresolved);
    }

    [Fact(DisplayName = "Only a translate token with no glossary entry and no translation needs the model")]
    public void NeedsModelTranslation_OnlyWhenNothingElseHasTheValue()
    {
        var plain = Slotter();
        var withGlossary = Slotter([new GlossaryLine(Farewell, "Until we meet")]);

        Assert.True(plain.NeedsModelTranslation("farewell", new TokenValue(Farewell)));
        Assert.False(plain.NeedsModelTranslation("farewell", new TokenValue(Farewell) { Translated = "See you" }));
        Assert.False(withGlossary.NeedsModelTranslation("farewell", new TokenValue(Farewell)));
        Assert.False(new TokenSlotter([new NamedTokenReplacer { Literal = "<x>", Name = "x" }], []).NeedsModelTranslation("x", new TokenValue("<x>")));
    }

    // ---------------------------------------------------------------- showValue

    [Fact(DisplayName = "showValue tells the model what the slot stands for, once it has a value")]
    public void ShowValue_DescribesTheSlot()
    {
        var slotter = Slotter();

        Assert.Contains("`{farewell}` stands for \"See you again\"", slotter.DescribeValues("{farewell}，朋友", Tokens(Farewell, "See you again")));
        Assert.Equal(string.Empty, slotter.DescribeValues("{farewell}，朋友", Tokens(Farewell)));
        Assert.Equal(string.Empty, slotter.DescribeValues("朋友", Tokens(Farewell, "See you again")));
    }

    [Fact(DisplayName = "A slot without showValue is not described by value")]
    public void ShowValue_OffByDefault()
    {
        var slotter = Slotter(null, Phrase(showValue: false));

        Assert.Equal(string.Empty, slotter.DescribeValues("{farewell}", Tokens(Farewell, "See you again")));
        Assert.Empty(slotter.ShownValues("{farewell}", Tokens(Farewell, "See you again")));
    }

    [Fact(DisplayName = "showValue on a glossary token shows the glossary Result")]
    public void ShowValue_GlossaryKind()
    {
        var slotter = new TokenSlotter([new NamedTokenReplacer { Literal = "<少侠||姑娘>", Name = "playerTitle", Kind = "glossary", ShowValue = true }],
            [new GlossaryLine("<少侠||姑娘>", "<Young Hero||Miss>")]);
        var tokens = new Dictionary<string, TokenValue> { ["playerTitle"] = new("<少侠||姑娘>") };

        Assert.Equal([("playerTitle", "<Young Hero||Miss>")], slotter.ShownValues("{playerTitle}", tokens));
    }

    // ---------------------------------------------------------------- translating values

    private static (LlmConfig Config, HttpClient Client, List<string> Requests) Model(
        Func<string, string> answer, params GlossaryLine[] glossary)
    {
        var requests = new List<string>();
        var config = new LlmConfig { RetryCount = 1, CorrectionPromptsEnabled = false };
        var model = ConfigurationExtensions.GetPresetModelConfig(ModelPreset.Qwen25, ModelPresetType.Standard);
        model.Url = "http://test.local/v1/chat/completions";
        model.ApiKeyRequired = false;
        config.Runtime.Models["Default"] = model;
        config.Runtime.GlossaryLines = [.. glossary];

        var handler = new AnsweringHandler(answer, requests);
        return (config, new HttpClient(handler), requests);
    }

    /// <summary>Answers every chat request from a function of the request's full content, and records the contents.</summary>
    private sealed class AnsweringHandler(Func<string, string> answer, List<string> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var content = string.Join('\n', doc.RootElement.GetProperty("messages").EnumerateArray()
                .Select(m => m.GetProperty("content").GetString() ?? string.Empty));
            lock (requests)
                requests.Add(content);

            var json = System.Text.Json.JsonSerializer.Serialize(new { choices = new object[] { new { message = new { content = answer(content) } } } });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private static TextFileToSplit File(string path = "Dialog.csv") => new() { Path = path, TextFileType = TextFileType.RawCsv, EnableGlossary = true, EnableBasePrompts = false };

    private static TranslationLine Line(string slottedText, string source = Farewell) => new()
    {
        Raw = $"1,{slottedText}",
        Splits = [new TranslationSplit(1, slottedText) { Tokens = Tokens(source) }],
    };

    [Fact(DisplayName = "A phrase repeated across lines and files is translated by the model once")]
    public async Task Phrase_IsTranslatedOnce()
    {
        var (config, client, requests) = Model(_ => "See you again");
        StringTokenReplacer.SetNamedTokens([Phrase()], []);
        var translator = new TokenValueTranslator(config, client);
        var lines = Enumerable.Range(0, 5).Select(i => Line($"{{farewell}}，朋友{i}")).ToList();
        var other = new List<TranslationLine> { Line("{farewell}，再见") };

        var filledFirst = await translator.EnsureAsync(File(), lines);
        var filledSecond = await translator.EnsureAsync(File(), other);

        Assert.Equal(5, filledFirst);
        Assert.Equal(1, filledSecond);
        Assert.Equal(1, translator.ModelCalls);
        Assert.Single(requests);
        Assert.All(lines.Concat(other).SelectMany(l => l.Splits), s => Assert.Equal("See you again", s.Tokens["farewell"].Translated));
    }

    [Fact(DisplayName = "A phrase with a glossary entry needs no model call and stores nothing")]
    public async Task Phrase_WithGlossaryEntry_NeedsNoModel()
    {
        var glossary = new GlossaryLine(Farewell, "Until we meet");
        var (config, client, requests) = Model(_ => throw new InvalidOperationException("the model must not be called"), glossary);
        StringTokenReplacer.SetNamedTokens([Phrase()], [glossary]);
        var translator = new TokenValueTranslator(config, client);
        var lines = new List<TranslationLine> { Line("{farewell}，朋友") };

        Assert.Equal(0, await translator.EnsureAsync(File(), lines));
        Assert.Empty(requests);
        Assert.Equal(string.Empty, lines[0].Splits[0].Tokens["farewell"].Translated);
        Assert.Equal("Until we meet，朋友", StringTokenReplacer.Slotter.Restore(lines[0].Splits[0].Text, lines[0].Splits[0].Tokens).Text);
    }

    [Fact(DisplayName = "A phrase with no glossary entry falls back to the model")]
    public async Task Phrase_WithoutGlossaryEntry_UsesTheModel()
    {
        var (config, client, requests) = Model(_ => "Farewell");
        StringTokenReplacer.SetNamedTokens([Phrase()], []);
        var translator = new TokenValueTranslator(config, client);
        var lines = new List<TranslationLine> { Line("{farewell}") };

        await translator.EnsureAsync(File(), lines);

        Assert.Single(requests);
        Assert.Equal("Farewell", lines[0].Splits[0].Tokens["farewell"].Translated);
    }

    [Fact(DisplayName = "The cache key includes the glossary: the same phrase under a different glossary is translated again")]
    public async Task Phrase_CacheKeyIncludesTheGlossary()
    {
        // 后会 only applies to A.csv, so the glossary block in the prompt differs between the two files.
        var glossary = new GlossaryLine("后会", "See") { OnlyOutputFiles = ["A.csv"] };
        var (config, client, requests) = Model(_ => "See you again", glossary);
        StringTokenReplacer.SetNamedTokens([Phrase()], []);
        var translator = new TokenValueTranslator(config, client);

        await translator.EnsureAsync(File("A.csv"), [Line("{farewell}一")]);
        await translator.EnsureAsync(File("A.csv"), [Line("{farewell}二")]);
        await translator.EnsureAsync(File("B.csv"), [Line("{farewell}三")]);

        Assert.Equal(2, translator.ModelCalls);
        Assert.Equal(2, requests.Count);
    }

    [Fact(DisplayName = "A phrase the model cannot translate stays empty and is retried next run")]
    public async Task Phrase_Failure_StaysEmpty()
    {
        var (config, client, _) = Model(_ => "还是中文");
        StringTokenReplacer.SetNamedTokens([Phrase()], []);
        var lines = new List<TranslationLine> { Line("{farewell}") };

        Assert.Equal(0, await new TokenValueTranslator(config, client).EnsureAsync(File(), lines));
        Assert.Equal(string.Empty, lines[0].Splits[0].Tokens["farewell"].Translated);
    }

    [Fact(DisplayName = "Nothing is translated when no translate token is configured")]
    public async Task NoTranslateToken_NothingToDo()
    {
        var (config, client, requests) = Model(_ => "x");
        StringTokenReplacer.SetNamedTokens([new NamedTokenReplacer { Literal = Farewell, Name = "farewell" }], []);

        Assert.Equal(0, await new TokenValueTranslator(config, client).EnsureAsync(File(), [Line("{farewell}")]));
        Assert.Empty(requests);
    }

    // ---------------------------------------------------------------- merge

    [Fact(DisplayName = "Merge keeps a translated token value for an unchanged source and drops it for a changed one")]
    public async Task Merge_KeepsTranslatedValuesBySource()
    {
        StringTokenReplacer.SetNamedTokens([Phrase()], []);
        Directory.CreateDirectory($"{_dir}/Converted");
        Directory.CreateDirectory($"{_dir}/Raw/Export");
        var file = File();

        TranslationLine Export(string source) => new()
        {
            Raw = "1,x",
            Splits = [new TranslationSplit(1, "{farewell}，朋友") { Tokens = Tokens(source) }],
        };

        var converted = Export(Farewell);
        converted.Splits[0].Translated = "{farewell}, friend";
        converted.Splits[0].Tokens["farewell"].Translated = "See you again";
        var serializer = YamlHelper.CreateSerializer();
        System.IO.File.WriteAllText($"{_dir}/Converted/{file.Path}.yaml", serializer.Serialize(new List<TranslationLine> { converted }));

        System.IO.File.WriteAllText($"{_dir}/Raw/Export/{file.Path}.yaml", serializer.Serialize(new List<TranslationLine> { Export(Farewell) }));
        await GameFileHandlingBase.MergeFilesIntoTranslatedAsync(_dir, [file]);
        var kept = YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(System.IO.File.ReadAllText($"{_dir}/Converted/{file.Path}.yaml"))[0].Splits[0];
        Assert.Equal("See you again", kept.Tokens["farewell"].Translated);
        Assert.Equal("{farewell}, friend", kept.Translated);

        System.IO.File.WriteAllText($"{_dir}/Raw/Export/{file.Path}.yaml", serializer.Serialize(new List<TranslationLine> { Export("珍重") }));
        await GameFileHandlingBase.MergeFilesIntoTranslatedAsync(_dir, [file]);
        var changed = YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(System.IO.File.ReadAllText($"{_dir}/Converted/{file.Path}.yaml"))[0].Splits[0];
        Assert.Equal("珍重", changed.Tokens["farewell"].Source);
        Assert.Equal(string.Empty, changed.Tokens["farewell"].Translated);
    }

    // ---------------------------------------------------------------- packaging

    private async Task<(string[] Lines, int RawFallback)> PackageAsync(params TranslationLine[] lines)
    {
        Directory.CreateDirectory($"{_dir}/Converted");
        Directory.CreateDirectory($"{_dir}/Glossary");
        System.IO.File.WriteAllText($"{_dir}/Config.yaml", """
            models:
              - name: Standard
                model: test-model
                url: "http://localhost/test"
            namedTokenReplacers:
              - literal: 后会有期
                name: farewell
                kind: translate
            """);
        var file = new TextFileToSplit { Path = "Dialog.csv", SkipColumns = [0] };
        System.IO.File.WriteAllText($"{_dir}/Converted/{file.Path}.yaml", YamlHelper.CreateSerializer().Serialize(lines.ToList()));

        var counts = await CsvGameDataWorkflow.PackageAsync(_dir, file, config: ConfigurationExtensions.GetConfiguration(_dir));
        return (System.IO.File.ReadAllLines($"{_dir}/Mod/{file.Path}"), counts.RawFallback);
    }

    private static TranslationLine Translated(string translated, string stored) => new()
    {
        Raw = $"1,{Farewell}，朋友",
        Splits = [new TranslationSplit(1, "{farewell}，朋友") { Translated = translated, Tokens = Tokens(Farewell, stored) }],
    };

    [Fact(DisplayName = "Packaging writes the stored translation of a translate token")]
    public async Task Packaging_UsesTheStoredTranslation()
    {
        var (lines, fallback) = await PackageAsync(Translated("{farewell}, friend", "See you again"));

        Assert.Equal(["1,\"See you again, friend\""], lines);
        Assert.Equal(0, fallback);
    }

    [Fact(DisplayName = "Packaging a translate token with no value yet falls back to raw")]
    public async Task Packaging_WithoutAValue_FallsBackToRaw()
    {
        var (lines, fallback) = await PackageAsync(Translated("{farewell}, friend", string.Empty));

        Assert.Equal([$"1,{Farewell}，朋友"], lines);
        Assert.Equal(1, fallback);
    }

    // ---------------------------------------------------------------- the translation run

    [Theory(DisplayName = "A translation run translates a repeated phrase once, tells the model its value, and stores it")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_TranslatesThePhraseOnceAndUsesItInThePrompt(bool pooled)
    {
        StringTokenReplacer.SetNamedTokens([Phrase()], []);
        var (config, client, requests) = Model(content =>
            content.Contains("{farewell}") ? "{farewell}, my friend"
            : content.Contains(Farewell) ? "See you again"
            : "unexpected request");
        config.UseContinuousWorkerPool = pooled;
        config.Runtime.Models["Default"].Prompts["DynamicPlaceholderPrompt"] = config.Runtime.Models["Default"].Prompts.GetValueOrDefault("DynamicPlaceholderPrompt", "Keep {n} slots.");

        var file = File();
        Directory.CreateDirectory($"{_dir}/Converted");
        Directory.CreateDirectory($"{_dir}/TestResults/OldFiles");
        var lines = new List<TranslationLine> { Line("{farewell}，朋友"), Line("{farewell}，朋友们") };
        System.IO.File.WriteAllText($"{_dir}/Converted/{file.Path}.yaml", YamlHelper.CreateSerializer().Serialize(lines));

        await TranslationService.TranslateViaLlmAsync(config, TranslationCorpus.Load(_dir, [file], copyMissingFromExport: false), false, client);

        var saved = YamlHelper.CreateDeserializer().Deserialize<List<TranslationLine>>(System.IO.File.ReadAllText($"{_dir}/Converted/{file.Path}.yaml"));
        Assert.Equal(1, requests.Count(r => !r.Contains("{farewell}") && r.Contains(Farewell)));
        Assert.All(saved.SelectMany(l => l.Splits), s =>
        {
            Assert.Equal("See you again", s.Tokens["farewell"].Translated);
            Assert.Equal("{farewell}, my friend", s.Translated);
        });
        Assert.Contains(requests, r => r.Contains("{farewell}") && r.Contains("stands for \"See you again\""));
    }
}
