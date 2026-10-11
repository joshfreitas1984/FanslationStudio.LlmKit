using System.Collections.Concurrent;
using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Translates the value of a <c>translate</c>-kind token once and stores it in the split's <c>tokens</c>
/// (<see cref="TokenValue.Translated"/>), so a phrase repeated across many lines is one model call and reads the same everywhere.
/// A glossary entry for the phrase wins and needs no call (it is read at packaging). The cache lives for one translation run and its
/// key is the source plus the glossary block that applies to it, so a changed glossary is a new key.
/// </summary>
internal sealed class TokenValueTranslator(LlmConfig config, HttpClient client)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _cache = new();
    private int _modelCalls;

    /// <summary>How many phrases went to the model (a cache hit or a glossary entry is not one).</summary>
    public int ModelCalls => Volatile.Read(ref _modelCalls);

    /// <summary>
    /// Fills <see cref="TokenValue.Translated"/> for every token in <paramref name="lines"/> that needs it, translating each distinct
    /// source once. Returns how many tokens were filled. A phrase the model cannot translate stays empty (packaging then sends the line to its raw
    /// fallback with a reason, and the next run tries again).
    /// </summary>
    public async Task<int> EnsureAsync(TextFileToSplit textFile, IEnumerable<TranslationLine> lines, int maxConcurrency = 4)
    {
        var slotter = StringTokenReplacer.Slotter;
        if (!slotter.HasTranslatedTokens)
            return 0;

        var pending = lines
            .SelectMany(l => l.Splits)
            .Where(s => s.SafeToTranslate && s.Text.Length > 0)
            .SelectMany(s => s.Tokens.Select(t => (Name: t.Key, Token: t.Value)))
            .Where(t => slotter.NeedsModelTranslation(t.Name, t.Token))
            .ToList();

        if (pending.Count == 0)
            return 0;

        await Parallel.ForEachAsync(pending.Select(p => p.Token.Source).Distinct().ToList(),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxConcurrency) },
            async (source, _) => await ResolveAsync(textFile, source));

        var filled = 0;
        foreach (var (_, token) in pending)
        {
            var translated = await ResolveAsync(textFile, token.Source);
            if (translated.Length > 0)
            {
                token.Translated = translated;
                filled++;
            }
        }

        return filled;
    }

    private Task<string> ResolveAsync(TextFileToSplit textFile, string source)
    {
        var glossary = GlossaryLine.AppendPromptsFor(source, config.Runtime.GlossaryLines, textFile.Path);
        var key = $"{source}\u0001{glossary}";

        return _cache.GetOrAdd(key, _ => new Lazy<Task<string>>(() => TranslateAsync(textFile, source))).Value;
    }

    private async Task<string> TranslateAsync(TextFileToSplit textFile, string source)
    {
        Interlocked.Increment(ref _modelCalls);

        try
        {
            var result = await TranslationService.TranslateSplitAsync(config, source, client, textFile);
            if (result.Valid && !string.IsNullOrWhiteSpace(result.Result) && !LineValidation.ContainsCjk(result.Result))
                return result.Result.Trim();

            Console.WriteLine($"Token value not translated: \"{source}\" -> \"{result.Result}\" ({result.CorrectionPrompt})");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            Console.WriteLine($"Token value not translated: \"{source}\" ({e.Message})");
        }

        return string.Empty;
    }
}
