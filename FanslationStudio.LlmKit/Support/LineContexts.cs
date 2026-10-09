using FanslationStudio.LlmKit.Configuration;
using System.Collections.Concurrent;

namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// Builds <see cref="RuntimeValues.LineContexts"/> from <see cref="GameHooks.LineContextProvider"/> and carries a
/// column's context into the quality control prompts. Shared by the translation and quality control passes so both
/// see the same speaker context for a line.
/// </summary>
internal static class LineContexts
{
    /// <summary>Separates the glossary block from the line-context block inside the one string every QC stage already receives.</summary>
    internal const string QcSeparator = "\n\u0001LINE-CONTEXT\u0001\n";

    /// <summary>
    /// Asks the provider for each file's per-line contexts. Runs single-threaded before any LLM call, so the workers only
    /// read. A no-op unless <see cref="LlmConfig.LineContextEnabled"/> and the game set a provider.
    /// </summary>
    public static void Build(LlmConfig config, string workingDirectory, IEnumerable<(TextFileToSplit TextFile, IReadOnlyList<TranslationLine> Lines)> files)
    {
        config.Runtime.LineContexts = new ConcurrentDictionary<TranslationSplit, LineContext>();

        var provider = config.Hooks?.LineContextProvider;
        if (!config.LineContextEnabled || provider == null)
            return;

        foreach (var (textFile, lines) in files)
            foreach (var (split, context) in provider(workingDirectory, textFile, lines))
                config.Runtime.LineContexts[split] = context;
    }

    /// <summary>The glossary block, plus (when the column's anchor split has one) its line context after <see cref="QcSeparator"/>.</summary>
    public static string WithColumnContext(LlmConfig config, string glossaryPrompt, TranslationSplit anchor) =>
        config.Runtime.LineContexts.TryGetValue(anchor, out var context) && context.Prompt.Length > 0
            ? glossaryPrompt + QcSeparator + context.Prompt
            : glossaryPrompt;
}
