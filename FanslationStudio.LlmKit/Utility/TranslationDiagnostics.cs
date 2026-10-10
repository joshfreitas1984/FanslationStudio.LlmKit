using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Re-runs the real translation path (same prompt, glossary, line context, retries and validation) on specific
/// source lines and writes a readable report of exactly what the model was sent and answered. For answering "why
/// does this line keep ending up flaggedForRetranslation / Failed?" without a full pipeline run. Needs a live LLM.
/// </summary>
public static class TranslationDiagnostics
{
    private const int MaxLoggedChars = 700;

    /// <summary>
    /// Diagnoses each text in <paramref name="rawTexts"/> (an exact <see cref="TranslationSplit.Text"/> from
    /// <paramref name="textFile"/>'s Converted yaml). For each one the report lists the resolved line context, the
    /// glossary entries injected, the full system prompt, then <paramref name="attempts"/> end-to-end runs with
    /// every HTTP request/response of the retry loop (long model output is truncated, which also hides
    /// degenerate repetition loops) and the final validity and rejection reason.
    /// </summary>
    public static async Task<string> DiagnoseAsync(
        LlmConfig config,
        TextFileToSplit textFile,
        string workingDirectory,
        IEnumerable<string> rawTexts,
        int attempts = 3,
        string? reportPath = null)
    {
        var corpus = TranslationCorpus.Load(workingDirectory, [textFile], copyMissingFromExport: false);
        var lines = corpus.Files.Single().Lines;
        LineContexts.Build(config, workingDirectory, [(textFile, lines)]);

        var splitsByText = lines.SelectMany(l => l.Splits).GroupBy(s => s.Text).ToDictionary(g => g.Key, g => g.First());
        var report = new StringBuilder();

        foreach (var raw in rawTexts)
        {
            report.AppendLine($"=========== {raw}");

            if (!splitsByText.TryGetValue(raw, out var split))
            {
                report.AppendLine($"NOT FOUND as a split Text in {textFile.Path} (the text must match exactly).\n");
                continue;
            }

            config.Runtime.LineContexts.TryGetValue(split, out var context);
            var contextPrompt = context?.Prompt ?? string.Empty;
            report.AppendLine($"Flags: flaggedForRetranslation={split.FlaggedForRetranslation} flaggedMistranslation={split.FlaggedMistranslation}");
            report.AppendLine($"Line context: {(contextPrompt.Length > 0 ? contextPrompt : "(none)")} GenderKnown={context?.GenderKnown}");

            var modelConfig = LlmHelpers.CalculateModelConfig(config, raw);
            var messages = TranslationService.GenerateBaseMessages(modelConfig, config.Runtime.GlossaryLines, raw, textFile, contextPrompt);
            report.AppendLine($"Model: {modelConfig.Model}\nGlossary injected:\n{GlossaryLine.AppendPromptsFor(raw, config.Runtime.GlossaryLines, textFile.Path)}");
            report.AppendLine("Prompt messages:\n" + JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var traffic = new StringBuilder();
                using var client = new HttpClient(new LoggingHandler(traffic)) { Timeout = TimeSpan.FromSeconds(300) };
                var result = await TranslationService.TranslateSplitAsync(config, raw, client, textFile, contextPrompt, split.Split, context?.GenderKnown ?? false);

                report.AppendLine($"##### attempt {attempt}/{attempts}: Valid={result.Valid}");
                report.AppendLine($"RESULT: {Truncate(result.Result)}");
                if (!result.Valid)
                    report.AppendLine($"REJECTED BECAUSE: {Truncate(result.CorrectionPrompt)}");
                report.AppendLine($"HTTP traffic (system prompt omitted, model output truncated):\n{traffic}");
            }
        }

        var text = report.ToString();
        if (reportPath != null)
            File.WriteAllText(reportPath, text);
        return text;
    }

    private static string Truncate(string text) =>
        text.Length <= MaxLoggedChars ? text : text[..MaxLoggedChars] + $"... [{text.Length - MaxLoggedChars} more chars]";

    /// <summary>Logs the user/assistant turns of each chat request and the model's answer; the (large, constant) system prompt is skipped.</summary>
    private sealed class LoggingHandler(StringBuilder log) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            var requestBody = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            log.AppendLine($"  >> {Summarise(requestBody, request: true)}");
            log.AppendLine($"  << {Summarise(responseBody, request: false)}");
            return response;
        }

        private static string Summarise(string json, bool request)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (request)
                {
                    var turns = root.GetProperty("messages").EnumerateArray()
                        .Where(m => m.GetProperty("role").GetString() != "system")
                        .Select(m => $"[{m.GetProperty("role").GetString()}] {Truncate(m.GetProperty("content").GetString() ?? string.Empty)}");
                    return string.Join("  ", turns);
                }

                return Truncate(root.GetProperty("message").GetProperty("content").GetString() ?? string.Empty);
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return Truncate(json);
            }
        }
    }
}
