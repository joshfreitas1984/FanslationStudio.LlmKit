using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit;

public static class TranslationService
{
    public const int BatchlessLog = 25;
    public const int BatchlessBuffer = 25;

    public static async Task FillTranslationCacheAsync(string workingDirectory,
        int charsToCache, ConcurrentDictionary<string, string> cache,
        LlmConfig config, TextFileToSplit[] textFiles)
    {
        // Add Manual adjustments 
        foreach (var k in config.Runtime.ManualTranslations)
            cache.TryAdd(k.Raw, k.Result);

        // Add Glossary Lines to Cache
        foreach (var line in config.Runtime.GlossaryLines)
        {
            cache.TryAdd(line.Raw, line.Result);
        }

        // File with old files
        var oldFolder = $"{workingDirectory}/TestResults/OldFiles";

        var deserializer = YamlHelper.CreateDeserializer();

        foreach (var file in Directory.EnumerateFiles(oldFolder))
        {
            var content = File.ReadAllText(file);
            var lines = deserializer.Deserialize<List<TranslationLine>>(content);

            foreach (var line in lines)
            {
                foreach (var split in line.Splits)
                {
                    cache.TryAdd(split.Text, split.Translated);
                }
            }
        }

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory,
            textFiles,
            async (outputFile, textFileToTranslate, fileLines) =>
        {
            foreach (var line in fileLines)
            {
                foreach (var split in line.Splits)
                {
                    if (string.IsNullOrEmpty(split.Translated) || split.FlaggedForRetranslation)
                        continue;

                    if (split.Text.Length <= charsToCache)
                        cache.TryAdd(split.Text, split.Translated);
                }
            }

            await Task.CompletedTask;
        });

        //Add it to config to make it easier to use
        config.Runtime.TranslationCache = cache;
    }

    public static async Task TranslateViaLlmAsync(string workingDirectory, bool forceRetranslation,
        TextFileToSplit[] textFiles)
    {
        string inputPath = $"{workingDirectory}/Raw/Export";
        string outputPath = $"{workingDirectory}/Converted";

        // Create output folder
        if (!Directory.Exists(outputPath))
            Directory.CreateDirectory(outputPath);

        var config = ConfigurationExtensions.GetConfiguration(workingDirectory);

        // Translation Cache - dedups repeated strings within this run and across history
        // (manual translations, glossary, TestResults/OldFiles, already-translated splits). Capped
        // by length rather than uncapped: short/medium repeated strings (names, common short
        // phrases) are very likely to recur elsewhere and are cheap to keep in memory, but longer
        // translations (40-50+ chars) are unlikely to repeat verbatim and aren't worth retaining -
        // an uncapped cache would just accumulate one-off long strings for no benefit. 50 was chosen
        // to comfortably cover short/medium repeated phrases while still excluding long one-off
        // sentences.
        // ConcurrentDictionary because splits within a batch are translated in parallel
        // (Task.WhenAll below) and each worker reads/writes this cache concurrently.
        var translationCache = new ConcurrentDictionary<string, string>();
        var charsToCache = 50;
        await FillTranslationCacheAsync(workingDirectory, charsToCache, translationCache, config, textFiles);

        // Create an HttpClient instance
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(300);

        int incorrectLineCount = 0;
        int totalRecordsProcessed = 0;

        foreach (var textFileToTranslate in textFiles)
        {
            var inputFile = $"{inputPath}/{textFileToTranslate.Path}";
            var outputFile = $"{outputPath}/{textFileToTranslate.Path}.yaml";

            if (!File.Exists(outputFile))
                File.Copy(inputFile, outputFile);

            var content = File.ReadAllText(outputFile);

            Console.WriteLine($"Processing File: {textFileToTranslate.Path}");

            var serializer = YamlHelper.CreateSerializer();
            var deserializer = YamlHelper.CreateDeserializer();
            var fileLines = deserializer.Deserialize<List<TranslationLine>>(content);

            var batchSize = config.BatchSize ?? 20;
            var totalLines = fileLines.Count;
            var stopWatch = Stopwatch.StartNew();
            int recordsProcessed = 0;
            int bufferedRecords = 0;

            int logProcessed = 0;

            for (int i = 0; i < totalLines; i += batchSize)
            {
                int batchRange = Math.Min(batchSize, totalLines - i);

                // Use a slice of the list directly
                var batch = fileLines.GetRange(i, batchRange);

                // Get Unique splits incase the batch has the same entry multiple times (eg. NPC Names)
                var uniqueSplits = batch.SelectMany(line => line.Splits)
                    .GroupBy(split => split.Text)
                    .Select(group => group.First())
                    .ToList(); // Materialize to prevent multiple enumerations;

                // Process the unique in parallel
                await Task.WhenAll(uniqueSplits.Select(async split =>
                {
                    if (string.IsNullOrEmpty(split.Text) || !split.SafeToTranslate)
                        return;

                    var cacheHit = translationCache.ContainsKey(split.Text)
                        // We use this for name files etc which will be in cache
                        && textFileToTranslate.EnableGlossary;

                    if (string.IsNullOrEmpty(split.Translated)
                        || forceRetranslation
                        || (config.TranslateFlagged && split.FlaggedForRetranslation))
                    {
                        var original = split.Translated;

                        if (cacheHit)
                            split.Translated = translationCache[split.Text];
                        else
                        {
                            var result = await TranslateSplitAsync(config, split.Text, client, textFileToTranslate);
                            split.Translated = result.Valid ? result.Result : string.Empty;
                        }

                        split.ResetFlags(split.Translated != original);
                        recordsProcessed++;
                        totalRecordsProcessed++;
                        bufferedRecords++;
                    }

                    if (string.IsNullOrEmpty(split.Translated))
                        incorrectLineCount++;
                    else
                    {
                        //Two translations could be doing this at the same time
                        if (!cacheHit && split.Text.Length <= charsToCache)
                            translationCache.TryAdd(split.Text, split.Translated);
                    }
                }));

                // Duplicates
                var duplicates = batch.SelectMany(line => line.Splits)
                    .GroupBy(split => split.Text)
                    .Where(group => group.Count() > 1);

                foreach (var splitDupes in duplicates)
                {
                    var firstSplit = splitDupes.First();

                    // Skip first one - it should be ok
                    foreach (var split in splitDupes.Skip(1))
                    {
                        if (split.Translated != firstSplit.Translated
                            || string.IsNullOrEmpty(split.Translated)
                            || forceRetranslation
                            || (config.TranslateFlagged && split.FlaggedForRetranslation))
                        {
                            split.Translated = firstSplit.Translated;
                            split.ResetFlags();
                            recordsProcessed++;
                            totalRecordsProcessed++;
                            bufferedRecords++;
                        }
                    }
                }

                logProcessed++;

                if (batchSize != 1 || (logProcessed % BatchlessLog == 0))
                    Console.WriteLine($"Line: {i + batchRange} of {totalLines} File: {textFileToTranslate.Path} Unprocessable: {incorrectLineCount} Processed: {totalRecordsProcessed}");

                if (bufferedRecords > BatchlessBuffer)
                {
                    Console.WriteLine($"Writing Buffer....");
                    File.WriteAllText(outputFile, serializer.Serialize(fileLines));
                    bufferedRecords = 0;
                }
            }

            var elapsed = stopWatch.ElapsedMilliseconds;
            var speed = recordsProcessed == 0 ? 0 : elapsed / recordsProcessed;
            Console.WriteLine($"Done: {totalLines} ({elapsed} ms ~ {speed}/line)");
            File.WriteAllText(outputFile, serializer.Serialize(fileLines));
        }
    }

    public static async Task<(bool split, string result)> SplitOnCharsIfNeededAsync(string splitCharacters, LlmConfig config, string raw, HttpClient client, TextFileToSplit textFile)
    {
        if (raw.Contains(splitCharacters))
        {
            var splits = raw.Split(splitCharacters);

            string suffix;

            if (splitCharacters == "-")
                suffix = " - ";
            else if (splitCharacters == ":")
                suffix = ": ";
            else
                suffix = splitCharacters;

            // Pieces are independent of each other - translate them concurrently instead of one
            // at a time. Order is preserved because Task.WhenAll returns results in the same order
            // as the input tasks array.
            var translations = await Task.WhenAll(splits.Select(split => TranslateSplitAsync(config, split, client, textFile)));

            // If any piece fails we have to kill the lot
            if (translations.Any(t => !t.Valid) && !config.SkipLineValidation)
                return (true, string.Empty);

            var builder = new StringBuilder();
            foreach (var trans in translations)
                builder.Append($"{trans.Result}{suffix}");

            var result = builder.ToString();

            // Remove the very last suffix that was added
            if (splits.Length > 1)
                return (true, result[..^suffix.Length]);
            else
                return (true, result);
        }

        return (false, string.Empty);
    }

    public static async Task<(bool split, string result)> SplitBracketsRegexIfNeededAsync(LlmConfig config,
        string raw, HttpClient client,
        TextFileToSplit textFile)
    {
        // Collect all matches across all patterns and sort by position so multiple bracket types in
        // the same string are all handled in a single pass (e.g. "天竺国《无量寿经》【副本】4000钱")
        var allMatches = config.SplitRegexPatterns
            .SelectMany(pattern => Regex.Matches(raw, pattern).Cast<Match>())
            .OrderBy(m => m.Index)
            .ToList();

        if (allMatches.Count == 0)
            return (false, string.Empty);

        // Discard overlapping matches up-front so the remaining set can be translated concurrently
        // without affecting each other's outcome.
        var nonOverlappingMatches = new List<Match>();
        var lastOriginalIndexForFilter = 0;
        foreach (var match in allMatches)
        {
            if (match.Index < lastOriginalIndexForFilter)
                continue;

            nonOverlappingMatches.Add(match);
            lastOriginalIndexForFilter = match.Index + match.Length;
        }

        // Pre-translate each match's inner content separately (independent of each other, so done
        // concurrently) and wrap it in single quotes as a placeholder (e.g. 'Sutra of Immeasurable
        // Life') so the LLM treats it as a proper noun and preserves it during full-sentence
        // translation. After translation, restore the original bracket characters by replacing
        // 'translatedText' with openBracket+translatedText+closeBracket.
        var innerTranslations = await Task.WhenAll(nonOverlappingMatches.Select(match =>
            TranslateSplitAsync(config, match.Value[1..^1], client, textFile)));

        if (innerTranslations.Any(t => !t.Valid) && !config.SkipLineValidation)
            return (true, string.Empty);

        var bracketRestorations = new List<(string quotedText, char openBracket, char closeBracket)>();
        var modifiedRaw = raw;
        var offset = 0;

        var matchIndex = 99;

        for (int matchPos = 0; matchPos < nonOverlappingMatches.Count; matchPos++)
        {
            var match = nonOverlappingMatches[matchPos];
            var openBracket = match.Value[0];
            var closeBracket = match.Value[^1];
            var innerTrans = innerTranslations[matchPos];

            var quotedText = $"{matchIndex++}";
            bracketRestorations.Add((quotedText, openBracket, closeBracket));

            var adjustedIndex = match.Index + offset;
            modifiedRaw = modifiedRaw[..adjustedIndex] + quotedText + modifiedRaw[(adjustedIndex + match.Length)..];
            offset += quotedText.Length - match.Length;
        }

        // Translate the full sentence with pre-translated placeholders to preserve surrounding context
        var fullTrans = await TranslateSplitAsync(config, modifiedRaw, client, textFile);
        if (!fullTrans.Valid && !config.SkipLineValidation)
            return (true, string.Empty);

        // Restore the original bracket characters: replace 'translatedText' with openBracket+translatedText+closeBracket
        var result = fullTrans.Result;
        foreach (var (quotedText, openBracket, closeBracket) in bracketRestorations)
            result = result
                .Replace(quotedText, $"{openBracket}{quotedText[1..^1]}{closeBracket}");

        return (true, result.Trim());
    }

    public static bool IsGameObjectReference(string raw)
    {
        // Check if it looks like a game object reference
        if (raw.Contains("/")
                && (raw.Contains("View")
                || raw.Contains("btn")
                || raw.Contains("Part")
                || raw.Contains("Text")))
            return true;
        return false;
    }


    public static async Task<ValidationResult> TranslateSplitAsync(LlmConfig config,
        string? raw,
        HttpClient client,
        TextFileToSplit textFile,
        string additionalPrompts = "")
    {
        if (string.IsNullOrEmpty(raw))
            return new ValidationResult(true, string.Empty); //Is ok because raw was empty

        var pattern = LineValidation.ChineseCharPattern;

        // If it is already translated or just special characters return it
        if (!Regex.IsMatch(raw, pattern))
            return new ValidationResult(true, raw);

        if (textFile.TextFileType == TextFileType.LocalTextString)
        {
            // Check if it looks like a game object reference
            if (IsGameObjectReference(raw))
                return new ValidationResult(true, raw);
        }

        // Prepare the raw by stripping out anything the LLM can't support
        var tokenReplacer = new StringTokenReplacer();
        var preparedRaw = LineValidation.PrepareRaw(raw, tokenReplacer);

        // If it is already translated or just special characters return it
        if (!Regex.IsMatch(preparedRaw, pattern))
            return new ValidationResult(true, LineValidation.CleanupLineBeforeSaving(preparedRaw, preparedRaw, textFile, tokenReplacer));

        var (regexSplit, regexResult) = await SplitBracketsRegexIfNeededAsync(config, raw, client, textFile);
        if (regexSplit)
            return new ValidationResult(LineValidation.CleanupLineBeforeSaving(regexResult, preparedRaw, textFile, tokenReplacer));

        // We do segementation here since saves context window by splitting // "。" doesnt work like u think it would        
        foreach (var splitCharacters in config.SplitCharactersList)
        {
            var (split, result) = await SplitOnCharsIfNeededAsync(splitCharacters, config, preparedRaw, client, textFile);

            // Because its recursive we want to bail out on the first successful one
            if (split)
                return new ValidationResult(LineValidation.CleanupLineBeforeSaving(result, preparedRaw, textFile, tokenReplacer));
        }

        if (ColorTagHelpers.StartsWithHalfColorTag(preparedRaw, out string start, out string end))
        {
            var startResult = await TranslateSplitAsync(config, start, client, textFile);
            var endResult = await TranslateSplitAsync(config, end, client, textFile);
            var combinedResult = $"{startResult.Result}{endResult.Result}";

            if (!config.SkipLineValidation && (!startResult.Valid || !endResult.Valid))
                return new ValidationResult(false, string.Empty);
            else
                return new ValidationResult(LineValidation.CleanupLineBeforeSaving($"{combinedResult}", preparedRaw, textFile, tokenReplacer));
        }

        var cacheHit = config.Runtime.TranslationCache.ContainsKey(preparedRaw);
        if (cacheHit)
            return new ValidationResult(LineValidation.CleanupLineBeforeSaving(config.Runtime.TranslationCache[preparedRaw], preparedRaw, textFile, tokenReplacer));

        // Calculate Executing model based on text
        var modelConfig = LlmHelpers.CalculateModelConfig(config, preparedRaw);

        // Define the request payload
        List<object> messages = GenerateBaseMessages(modelConfig, config.Runtime.GlossaryLines, preparedRaw, textFile, additionalPrompts);

        try
        {
            var retryCount = 0;
            var preparedResult = string.Empty;
            var validationResult = new ValidationResult();

            while (!validationResult.Valid && retryCount < (config.RetryCount ?? 1))
            {
                var llmResult = await TranslateMessagesAsync(client, config, modelConfig, messages);
                preparedResult = LineValidation.PrepareResult(preparedRaw, llmResult);
                validationResult = LineValidation.CheckTransalationSuccessful(modelConfig, preparedRaw, preparedResult, textFile);
                validationResult.Result = LineValidation.CleanupLineBeforeSaving(validationResult.Result, preparedRaw, textFile, tokenReplacer);

                if (config.SkipLineValidation)
                    validationResult.Valid = true;

                // Append history of failures
                if (!validationResult.Valid && config.CorrectionPromptsEnabled)
                {
                    // Use sentence-by-sentence correction for Chinese character issues
                    if (validationResult.RequiresSentenceBySentenceCorrection)
                    {
                        var correctedResult = await CorrectSentenceBySentenceAsync(client, config, modelConfig, preparedRaw, llmResult, textFile);
                        preparedResult = LineValidation.PrepareResult(preparedRaw, correctedResult);
                        validationResult = LineValidation.CheckTransalationSuccessful(modelConfig, preparedRaw, preparedResult, textFile);
                        validationResult.Result = LineValidation.CleanupLineBeforeSaving(validationResult.Result, preparedRaw, textFile, tokenReplacer);

                        if (config.SkipLineValidation)
                            validationResult.Valid = true;

                        // If sentence-by-sentence correction succeeded, break out of retry loop
                        // If it still failed, regenerate messages with the corrected result for next retry
                        if (!validationResult.Valid)
                        {
                            messages = GenerateBaseMessages(modelConfig, config.Runtime.GlossaryLines, preparedRaw, textFile);
                            var correctionPrompt = CalulateCorrectionPrompt(modelConfig, validationResult, preparedRaw, correctedResult);
                            AddCorrectionMessages(messages, correctedResult, correctionPrompt);
                        }
                    }
                    else
                    {
                        var correctionPrompt = CalulateCorrectionPrompt(modelConfig, validationResult, preparedRaw, llmResult);

                        // Regenerate base messages so we dont hit token limit by constantly appending retry history
                        messages = GenerateBaseMessages(modelConfig, config.Runtime.GlossaryLines, preparedRaw, textFile);
                        AddCorrectionMessages(messages, llmResult, correctionPrompt);
                    }
                }

                retryCount++;
            }

            return validationResult;
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"Request error: {e.Message}");
            return new ValidationResult(string.Empty);
        }
    }
    public static void AddCorrectionMessages(List<object> messages, string result, string correctionPrompt)
    {
        messages.Add(LlmHelpers.GenerateAssistantPrompt(result));
        messages.Add(LlmHelpers.GenerateUserPrompt(correctionPrompt));
    }

    public static async Task<string> CorrectSentenceBySentenceAsync(HttpClient client, LlmConfig config, ModelExecutionConfig executingModel, string raw, string failedResult, TextFileToSplit textFile)
    {
        // Split the failed result by sentences (period followed by space or end of string)
        var sentences = failedResult.Split(new[] { ". " }, StringSplitOptions.None);

        // Sentences are independent of each other - correct them concurrently instead of one at a
        // time. Task.WhenAll preserves input order in its result array.
        var correctedSentences = await Task.WhenAll(sentences.Select(async (sentence, i) =>
        {
            // Add period back if not the last sentence
            if (i < sentences.Length - 1)
                sentence += ".";

            // Only correct sentences that contain Chinese characters
            if (Regex.IsMatch(sentence, LineValidation.ChineseCharPattern) && !Regex.IsMatch(sentence, LineValidation.ChinesePlaceholderPattern))
            {
                // For individual sentence correction, use a minimal prompt without the full original text
                // This prevents the LLM from re-translating everything
                var messages = new List<object>
                {
                    LlmHelpers.GenerateSystemPrompt(executingModel.Prompts["BaseSystemPrompt"]),
                    LlmHelpers.GenerateUserPrompt("The following sentence contains untranslated Chinese characters. Translate all Chinese characters to English while keeping the rest of the sentence intact."),
                    LlmHelpers.GenerateAssistantPrompt(sentence),
                    LlmHelpers.GenerateUserPrompt("Translate all Chinese characters in this sentence to English. " + executingModel.Prompts["BaseCorrectionSuffixPrompt"])
                };

                var correctedSentence = await TranslateMessagesAsync(client, config, executingModel, messages);
                return correctedSentence.Trim();
            }

            // Sentence is fine, keep it as is
            return sentence;
        }));

        // Rejoin sentences with proper spacing
        return string.Join(" ", correctedSentences);
    }

    public static List<object> GenerateBaseMessages(ModelExecutionConfig config, List<GlossaryLine> glossaryLines, string raw, TextFileToSplit splitFile, string additionalSystemPrompt = "")
    {
        //Dynamically build prompt using whats in the raws
        var basePrompt = new StringBuilder();

        if (splitFile.EnableBasePrompts)
        {
            basePrompt.AppendLine(config.Prompts["BaseSystemPrompt"]);

            if (raw.Contains("<color"))
                basePrompt.AppendLine(config.Prompts["DynamicColorPrompt"]);
            else if (raw.Contains("</color>"))
                basePrompt.AppendLine(config.Prompts["DynamicCloseColorPrompt"]);

            // Qwen 2.5 hates size tags
            if (raw.Contains("<size"))
                basePrompt.AppendLine(config.Prompts["DynamicSizePrompt"]);
            else if (raw.Contains("</size>"))
                basePrompt.AppendLine(config.Prompts["DynamicCloseSizePrompt"]);

            //if (raw.Contains("·"))
            //    basePrompt.AppendLine(config.Prompts["DynamicSegement1Prompt"]);

            if (raw.Contains("<"))
            {
                var rawTags = HtmlTagHelpers.ExtractTagsListWithAttributes(raw, "color", "size");
                if (rawTags.Count > 0)
                {
                    var prompt = string.Format(config.Prompts["DynamicTagPrompt"], string.Join("\n", rawTags));
                    basePrompt.AppendLine(prompt);
                }
            }

            if (raw.Contains('{'))
                basePrompt.AppendLine(config.Prompts["DynamicPlaceholderPrompt"]);
        }

        if (!string.IsNullOrEmpty(splitFile.AdditionalPromptName))
            basePrompt.AppendLine(config.Prompts[splitFile.AdditionalPromptName]);
        basePrompt.AppendLine(additionalSystemPrompt);

        if (splitFile.EnableGlossary)
        {
            basePrompt.AppendLine("");
            basePrompt.AppendLine(config.Prompts["BaseGlossaryPrompt"]);
            basePrompt.AppendLine(GlossaryLine.AppendPromptsFor(raw, glossaryLines, splitFile.Path));
        }

        if (splitFile.EnableBasePrompts)
        {
            basePrompt.AppendLine("");
            basePrompt.AppendLine(config.Prompts["BaseSystemSuffixPrompt"]);
        }

        return
        [
            LlmHelpers.GenerateSystemPrompt(basePrompt.ToString()),
            LlmHelpers.GenerateUserPrompt(raw)
        ];
    }

    public static string CalulateCorrectionPrompt(ModelExecutionConfig modelConfig, ValidationResult validationResult, string raw, string result)
    {
        // Return the concatenated specific correction prompts with the shared suffix
        // Context is provided by conversation structure (User: original, Assistant: failed attempt, User: corrections)
        if (string.IsNullOrEmpty(validationResult.CorrectionPrompt))
            return string.Empty;

        return validationResult.CorrectionPrompt + modelConfig.Prompts["BaseCorrectionSuffixPrompt"];
    }

    public static void AddPromptWithValues(this StringBuilder builder, ModelExecutionConfig config, string promptName, params string[] values)
    {
        var prompt = string.Format(config.Prompts[promptName], values);
        builder.Append(' ');
        builder.Append(prompt);
    }

    public static async Task<string> TranslateMessagesAsync(HttpClient client, LlmConfig config, ModelExecutionConfig modelToUse, List<object> messages)
    {
        // Generate based on what would have been created
        var requestData = LlmHelpers.GenerateLlmRequestData(modelToUse, messages);

        // Send correction & Get result
        HttpContent content = new StringContent(requestData, Encoding.UTF8, "application/json");

        try
        {
            // Set Bearer token if required and not already set
            var requiresApiKey = modelToUse.ApiKeyRequired ?? false;

            if (requiresApiKey)
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", modelToUse.ApiKey);
            else
                client.DefaultRequestHeaders.Authorization = null;

            HttpResponseMessage response = await client.PostAsync(modelToUse.Url, content);
            string responseBody = await response.Content.ReadAsStringAsync();

            if ((int)response.StatusCode == 429)
            {
                // Too Many Requests - simple exponential backoff
                int retryDelay = 5000; // start with 2 seconds
                int maxDelay = 60000; // max 30 seconds
                int retries = 0;
                var backoffStopwatch = Stopwatch.StartNew();
                while ((int)response.StatusCode == 429 && retries < 5)
                {
                    Console.WriteLine($"Received 429 Too Many Requests. Backing off attempt {retries + 1}/5, waiting {retryDelay}ms...");
                    await Task.Delay(retryDelay);
                    retryDelay = Math.Min(retryDelay * 2, maxDelay);
                    response = await client.PostAsync(modelToUse.Url, content);
                    responseBody = await response.Content.ReadAsStringAsync();
                    retries++;
                }

                if (retries > 0)
                    Console.WriteLine($"429 backoff finished after {retries} attempt(s), {backoffStopwatch.ElapsedMilliseconds}ms blocked, final status {(int)response.StatusCode}.");
            }

            response.EnsureSuccessStatusCode();

            using var jsonDoc = JsonDocument.Parse(responseBody);

            var result = string.Empty;

            if (responseBody.Contains("\"choices\":"))
            {
                result = jsonDoc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString()
                    ?.Trim() ?? string.Empty;
            }
            else
            {
                result = jsonDoc.RootElement
                    .GetProperty("message")!
                    .GetProperty("content")!
                    .GetString()
                    ?.Trim() ?? string.Empty;
            }

            // Remove any <think> tags and their content
            result = RemoveThinkTags(result);

            return result;
        }
        catch (Exception e)
        {
            if (config.SkipLineValidation)
            {
                Console.WriteLine($"Exception on: {requestData}");
                Console.WriteLine($"Exception message: {e.Message}");
                return "";
            }
            else
                throw;
        }
    }

    private static string RemoveThinkTags(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Regex to remove <think>...</think> tags and their content, including multiline
        return Regex.Replace(input, @"<think>.*?</think>\n\n", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
    }
}
