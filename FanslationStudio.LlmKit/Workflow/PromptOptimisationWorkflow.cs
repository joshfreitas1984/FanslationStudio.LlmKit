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
    /// Whole-file attempts to spend correcting a rejected suggestion (feeding back exactly why it
    /// was rejected, the same "assistant reply + corrective user message" shape
    /// <see cref="TranslationService.AddCorrectionMessages"/> uses for translation retries) before
    /// giving up and leaving the file untouched. Without this, the files that most need a good
    /// rewrite - the long, multi-rule ones like BaseSystemPrompt/BaseQualityReviewPrompt - are
    /// exactly the ones a single-shot attempt is most likely to fail the guards on and skip
    /// entirely.
    /// </summary>
    private const int MaxAttemptsPerFile = 3;

    /// <summary>
    /// Runs the optimisation pass over every *.txt file under
    /// <paramref name="baseFilesSourceRoot"/>/&lt;preset&gt;/ (recursively - covers Prompts/,
    /// Corrections/, Dynamics/), calling the preset's own real model for each rewrite and
    /// overwriting the file in place. Returns the paths of every file actually rewritten (a file
    /// is left untouched if every attempt fails validation - see <see cref="MaxAttemptsPerFile"/>).
    /// </summary>
    /// <param name="promptKeys">
    /// If set, only re-optimise files whose name (without extension - e.g. "BaseSystemPrompt")
    /// matches one of these, case-insensitively - every other file under the preset is left
    /// completely alone. Use this for a targeted re-run against files already committed from a
    /// prior full pass, so they aren't fed back through another round of shrinking. Omit (or pass
    /// null) to run every prompt file under the preset.
    /// </param>
    public static async Task<List<string>> RunAsync(ModelPreset preset, ModelPresetType presetType, string baseFilesSourceRoot,
        IReadOnlyCollection<string>? promptKeys = null)
    {
        var modelConfig = ConfigurationExtensions.GetPresetModelConfig(preset, presetType);
        var presetDir = Path.Combine(baseFilesSourceRoot, preset.ToString());

        if (!Directory.Exists(presetDir))
            throw new InvalidOperationException($"No BaseFiles source directory found at '{presetDir}'.");

        var promptFiles = Directory.GetFiles(presetDir, "*.txt", SearchOption.AllDirectories).OrderBy(p => p).ToList();

        if (promptKeys != null)
        {
            var keySet = new HashSet<string>(promptKeys, StringComparer.OrdinalIgnoreCase);
            promptFiles = promptFiles.Where(f => keySet.Contains(Path.GetFileNameWithoutExtension(f))).ToList();
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var config = new LlmConfig();
        var updated = new List<string>();

        foreach (var promptFile in promptFiles)
        {
            var original = await File.ReadAllTextAsync(promptFile);
            if (string.IsNullOrWhiteSpace(original))
                continue;

            // A file that is nothing but placeholder token(s) (e.g. BaseCorrectionPrompt.txt's
            // bare `{2}`) has no actual wording to tighten - asking the model to "optimise" it
            // anyway invites exactly what happened in practice: a fluent-looking but entirely
            // fabricated prompt (invented {0}/{1} substitutions, SCORE:/CORRECTED: labels that
            // don't belong to this file) that still passes the placeholder-presence check below
            // because the original {2} incidentally appears somewhere in the invention too.
            if (PlaceholderTokenPattern.Replace(original, "").Trim().Length == 0)
            {
                Console.WriteLine($"Prompt optimisation: skipping '{promptFile}' - it's just placeholder token(s) with no wording to optimise. Leaving it untouched.");
                continue;
            }

            var suggestion = await TryOptimisePromptAsync(client, config, modelConfig, promptFile, original);

            if (suggestion == null)
                continue;

            await File.WriteAllTextAsync(promptFile, suggestion);
            updated.Add(promptFile);
            Console.WriteLine($"Prompt optimisation: rewrote '{promptFile}' ({original.Length} -> {suggestion.Length} chars).");
        }

        Console.WriteLine($"Prompt optimisation done: {updated.Count} of {promptFiles.Count} file(s) under '{presetDir}' rewritten - review with `git diff` before committing.");

        return updated;
    }

    /// <summary>
    /// One prompt file's optimisation attempt loop - up to <see cref="MaxAttemptsPerFile"/> tries,
    /// feeding the specific validation failure back to the model each time (same shape as
    /// <see cref="TranslationService.AddCorrectionMessages"/>) rather than starting fresh, so a
    /// second attempt actually knows what it got wrong. Returns null (never writes anything) if
    /// every attempt fails validation, or the request itself keeps failing.
    /// </summary>
    private static async Task<string?> TryOptimisePromptAsync(HttpClient client, LlmConfig config,
        ModelExecutionConfig modelConfig, string promptFile, string original)
    {
        var promptKey = Path.GetFileNameWithoutExtension(promptFile);
        var messages = new List<object>
        {
            LlmHelpers.GenerateSystemPrompt(
                $"You are optimising your own prompt set. The prompt below (key: '{promptKey}') " +
                "is one you will be given verbatim ahead of future translation requests."),
            LlmHelpers.GenerateUserPrompt($"{OptimiseInstruction}\n\n---\n{original}"),
        };

        for (var attempt = 1; attempt <= MaxAttemptsPerFile; attempt++)
        {
            string suggestion;
            try
            {
                suggestion = await TranslationService.TranslateMessagesAsync(client, config, modelConfig, messages);
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"Prompt optimisation request error for '{promptFile}' (attempt {attempt}/{MaxAttemptsPerFile}): {e.Message}");
                continue;
            }

            var failureReason = ValidateSuggestion(original, suggestion);
            if (failureReason == null)
                return suggestion;

            Console.WriteLine($"Prompt optimisation: attempt {attempt}/{MaxAttemptsPerFile} for '{promptFile}' rejected - {failureReason}");

            if (attempt == MaxAttemptsPerFile)
            {
                Console.WriteLine($"Prompt optimisation: giving up on '{promptFile}' after {MaxAttemptsPerFile} attempt(s). Leaving it untouched. Last rejected suggestion:\n{suggestion}");
                return null;
            }

            messages.Add(LlmHelpers.GenerateAssistantPrompt(suggestion));
            messages.Add(LlmHelpers.GenerateUserPrompt(
                $"That rewrite is rejected: {failureReason} Try again - keep every rule and literal " +
                "token from the original, just tighten the wording further."));
        }

        return null;
    }

    /// <summary>
    /// Same checks as before, extracted so both the first attempt and every retry in
    /// <see cref="TryOptimisePromptAsync"/> validate identically. Returns null if the suggestion is
    /// acceptable, otherwise a human-readable reason suitable both for the console log and for
    /// feeding straight back to the model as corrective feedback.
    /// </summary>
    private static string? ValidateSuggestion(string original, string suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion))
            return "the response was empty.";

        var missingPlaceholders = PlaceholderTokenPattern.Matches(original)
            .Select(m => m.Value)
            .Distinct()
            .Where(token => !suggestion.Contains(token))
            .ToList();

        if (missingPlaceholders.Count > 0)
            return $"it dropped placeholder(s) {string.Join(", ", missingPlaceholders)} present in the original.";

        if (original.Length >= LongPromptThresholdChars && suggestion.Length < original.Length * MinAcceptableLengthRatioForLongPrompts)
            return $"it came back at {suggestion.Length} chars, under {MinAcceptableLengthRatioForLongPrompts:P0} of the original's {original.Length} chars - that likely means it dropped rules rather than just tightening wording.";

        return null;
    }
}
