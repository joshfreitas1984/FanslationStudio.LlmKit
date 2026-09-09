using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Utility;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Asks a preset's own model to rewrite its own prompt files (BaseSystemPrompt, Corrections,
/// Dynamics, BaseQualityReviewPrompt, etc. - every *.txt under BaseFiles/&lt;preset&gt;/) for
/// lower token cost/context usage without changing behaviour, and overwrites each source file in
/// place with the suggestion. This edits git-tracked source files directly - review the result
/// with `git diff` in the LlmKit repo and commit or discard per file, the same as any other
/// source edit; nothing here commits anything itself. Calls the preset's real model/url from its
/// embedded BaseFiles/&lt;preset&gt;/Config.yaml (see
/// <see cref="ConfigurationExtensions.GetPresetModelConfig"/>), so this talks to Ollama for
/// real - only run it manually, never as part of an automated/CI test suite.
/// </summary>
public static class PromptOptimisationWorkflow
{
    // Deliberately conservative after an earlier run (asking only for "fewer tokens/less context")
    // gutted BaseSystemPrompt.txt (1543 -> 121 chars) and BaseQualityReviewPrompt.txt (2296 -> 418
    // chars), dropping whole rules and the literal SCORE:/CORRECTED: labels QualityReviewWorkflow's
    // regexes depend on - "optimise for brevity" alone reads as license to summarize away content
    // and break callers that parse specific literal tokens out of the response. This wording asks
    // for tighter phrasing per rule, never fewer rules, and is paired with the placeholder-token
    // and length-ratio guards below that skip (rather than blindly write) a suggestion that looks
    // like it dropped something anyway.
    private const string OptimiseInstruction =
        "Rewrite the following prompt using tighter, more concise phrasing, so the model executing " +
        "it (you) spends fewer tokens reading it. Do not remove, merge, or summarize away any " +
        "individual rule, instruction, or requirement - every distinct rule in the original must " +
        "still be present as its own instruction, just worded more concisely. Keep every literal " +
        "token unchanged and in the exact same position/spelling, including placeholders like " +
        "`{0}`, `{1}`, `{2}` and any ALL-CAPS label followed by a colon (e.g. `SCORE:`, " +
        "`CORRECTED:`) - these are parsed by code and must not be altered, reworded, or dropped. " +
        "If the prompt is already concise, return it unchanged. Avoid numbering. Return only the " +
        "rewritten prompt text - no explanations, analysis, or markdown fences.";

    /// <summary>
    /// Placeholder tokens (`{0}`, `{1}`, ...) that a prompt may be run through
    /// <c>string.Format</c> with (see <see cref="TranslationService.AddPromptWithValues"/>) - a
    /// suggestion that drops one of these silently breaks whatever value was meant to be
    /// substituted in, even though the response still reads as fluent, valid-looking text.
    /// </summary>
    private static readonly Regex PlaceholderTokenPattern = new(@"\{\d+\}", RegexOptions.Compiled);

    /// <summary>
    /// A suggestion for a substantial (&gt;= <see cref="LongPromptThresholdChars"/> char) prompt
    /// that comes back under this fraction of the original's length is treated as suspect - in
    /// practice this is what caught both real regressions in the first run (a multi-rule prompt
    /// collapsed to a single generic sentence, technically fluent but missing most of its actual
    /// rules). Short one-line prompts are exempt since a large *ratio* drop there is often just a
    /// legitimately tighter paraphrase of a single idea.
    /// </summary>
    private const double MinAcceptableLengthRatioForLongPrompts = 0.5;
    private const int LongPromptThresholdChars = 500;

    /// <summary>
    /// Runs the optimisation pass over every *.txt file under
    /// <paramref name="baseFilesSourceRoot"/>/&lt;preset&gt;/ (recursively - covers Prompts/,
    /// Corrections/, Dynamics/), calling the preset's own real model for each rewrite and
    /// overwriting the file in place. Returns the paths of every file actually rewritten (a file
    /// is left untouched if the request fails or the model returns an empty response).
    /// </summary>
    public static async Task<List<string>> RunAsync(ModelPreset preset, ModelPresetType presetType, string baseFilesSourceRoot)
    {
        var modelConfig = ConfigurationExtensions.GetPresetModelConfig(preset, presetType);
        var presetDir = Path.Combine(baseFilesSourceRoot, preset.ToString());

        if (!Directory.Exists(presetDir))
            throw new InvalidOperationException($"No BaseFiles source directory found at '{presetDir}'.");

        var promptFiles = Directory.GetFiles(presetDir, "*.txt", SearchOption.AllDirectories).OrderBy(p => p).ToList();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var config = new LlmConfig();
        var updated = new List<string>();

        foreach (var promptFile in promptFiles)
        {
            var original = await File.ReadAllTextAsync(promptFile);
            if (string.IsNullOrWhiteSpace(original))
                continue;

            var promptKey = Path.GetFileNameWithoutExtension(promptFile);
            var messages = new List<object>
            {
                LlmHelpers.GenerateSystemPrompt(
                    $"You are optimising your own prompt set. The prompt below (key: '{promptKey}') " +
                    "is one you will be given verbatim ahead of future translation requests."),
                LlmHelpers.GenerateUserPrompt($"{OptimiseInstruction}\n\n---\n{original}"),
            };

            string suggestion;
            try
            {
                suggestion = await TranslationService.TranslateMessagesAsync(client, config, modelConfig, messages);
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"Prompt optimisation request error for '{promptFile}': {e.Message}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(suggestion))
            {
                Console.WriteLine($"Prompt optimisation: empty response for '{promptFile}' - leaving it untouched.");
                continue;
            }

            var missingPlaceholders = PlaceholderTokenPattern.Matches(original)
                .Select(m => m.Value)
                .Distinct()
                .Where(token => !suggestion.Contains(token))
                .ToList();

            if (missingPlaceholders.Count > 0)
            {
                Console.WriteLine($"Prompt optimisation: skipping '{promptFile}' - suggestion dropped placeholder(s) {string.Join(", ", missingPlaceholders)} present in the original. Leaving it untouched.");
                continue;
            }

            if (original.Length >= LongPromptThresholdChars && suggestion.Length < original.Length * MinAcceptableLengthRatioForLongPrompts)
            {
                Console.WriteLine($"Prompt optimisation: skipping '{promptFile}' - suggestion ({suggestion.Length} chars) is under {MinAcceptableLengthRatioForLongPrompts:P0} of the original ({original.Length} chars), which likely means it dropped rules rather than just tightening wording. Leaving it untouched.");
                continue;
            }

            await File.WriteAllTextAsync(promptFile, suggestion);
            updated.Add(promptFile);
            Console.WriteLine($"Prompt optimisation: rewrote '{promptFile}' ({original.Length} -> {suggestion.Length} chars).");
        }

        Console.WriteLine($"Prompt optimisation done: {updated.Count} of {promptFiles.Count} file(s) under '{presetDir}' rewritten - review with `git diff` before committing.");

        return updated;
    }
}
