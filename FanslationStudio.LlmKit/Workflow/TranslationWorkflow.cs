using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Workflow;

public static class TranslationWorkflow
{
    public static async Task ApplyAllRulesToCurrentTranslation(string workingDirectory, TextFileToSplit[] textFiles)
    {
        await UpdateCurrentTranslationLines(workingDirectory, true, textFiles);
    }

    public static async Task TranslateLines(string workingDirectory, TextFileToSplit[] textFiles)
    {
        await PerformTranslateLines(workingDirectory, false, textFiles);
    }

    public static async Task TranslateLinesBruteForce(string workingDirectory, TextFileToSplit[] textFiles)
    {
        await PerformTranslateLines(workingDirectory, true, textFiles);
    }

    private static async Task PerformTranslateLines(string workingDirectory, bool keepCleaning, TextFileToSplit[] textFileToSplits)
    {
        if (!keepCleaning)
        {
            await TranslationService.TranslateViaLlmAsync(workingDirectory, false, textFileToSplits);
            return;
        }

        PrintSeparator();
        int remaining = await UpdateCurrentTranslationLines(workingDirectory, false, textFileToSplits);
        PrintSeparator();

        int iterations = 0;
        while (remaining > 0 && iterations < 30)
        {
            await TranslationService.TranslateViaLlmAsync(workingDirectory, false, textFileToSplits);
            PrintSeparator();
            remaining = await UpdateCurrentTranslationLines(workingDirectory, false, textFileToSplits);
            PrintSeparator();
            iterations++;
        }
    }

    private static void PrintSeparator()
    {
        Console.WriteLine("-------------------------------------------------------------------");
        Console.WriteLine("-------------------------------------------------------------------");
    }

    private static async Task<int> UpdateCurrentTranslationLines(string workingDirectory, bool resetFlag, TextFileToSplit[] textFileToSplits)
    {
        var context = BuildTranslationRuleContext(workingDirectory);
        var totalRecordsModded = 0;
        var logLines = new ConcurrentBag<string>();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory, textFileToSplits, async (outputFile, textFile, fileLines) =>
        {
            int recordsModded = await ProcessFileAsync(outputFile, textFile, fileLines, resetFlag, logLines, context);
            Interlocked.Add(ref totalRecordsModded, recordsModded);
        });

        Console.WriteLine($"Total Lines: {totalRecordsModded} records");
        await File.WriteAllLinesAsync($"{workingDirectory}/TestResults/LineValidationLog.txt", logLines);

        return totalRecordsModded;
    }

    private record TranslationRuleContext(
        LlmConfig Config,
        HashSet<string> FullFileRetrans,
        List<string> NewGlossaryStrings,
        List<Regex> CompiledBadRegexes,
        Regex ChineseCharRegex);

    private static TranslationRuleContext BuildTranslationRuleContext(string workingDirectory)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory);

        HashSet<string> fullFileRetrans = [];
        List<string> newGlossaryStrings = [];
        var badRegexes = new List<string>
        {
            //"<size=[^>]+>"
            //@"master and disciple"
        };

        var compiledBadRegexes = badRegexes.Select(r => new Regex(r, RegexOptions.Compiled | RegexOptions.IgnoreCase)).ToList();
        var chineseCharRegex = new Regex(LineValidation.ChineseCharPattern, RegexOptions.Compiled);

        return new TranslationRuleContext(config, fullFileRetrans, newGlossaryStrings, compiledBadRegexes, chineseCharRegex);
    }

    private static async Task<int> ProcessFileAsync(
        string outputFile,
        TextFileToSplit textFile,
        List<TranslationLine> fileLines,
        bool resetFlag,
        ConcurrentBag<string> logLines,
        TranslationRuleContext context)
    {
        int recordsModded = 0;
        bool isFullFileRetrans = context.FullFileRetrans.Contains(textFile.Path);

        Parallel.ForEach(fileLines, line =>
        {
            int lineModded = ProcessLine(line, textFile, isFullFileRetrans, resetFlag, logLines, context);
            Interlocked.Add(ref recordsModded, lineModded);
        });

        if (recordsModded > 0 || resetFlag)
        {
            Console.WriteLine($"Writing {recordsModded} records to {outputFile}");
            var serializer = YamlHelper.CreateSerializer();
            await File.WriteAllTextAsync(outputFile, serializer.Serialize(fileLines));
        }

        return recordsModded;
    }

    private static int ProcessLine(
        TranslationLine line,
        TextFileToSplit textFile,
        bool isFullFileRetrans,
        bool resetFlag,
        ConcurrentBag<string> logLines,
        TranslationRuleContext context)
    {
        var tokenReplacer = new StringTokenReplacer();
        int modded = 0;

        foreach (var split in line.Splits)
        {
            if (resetFlag)
                split.ResetFlags(false);

            if (isFullFileRetrans)
            {
                split.FlaggedForRetranslation = true;
                modded++;
                continue;
            }

            if (UpdateSplit(logLines, context.NewGlossaryStrings, context.CompiledBadRegexes, split, textFile, context.Config, context.ChineseCharRegex, tokenReplacer))
                modded++;
        }

        return modded;
    }

    public static bool UpdateSplit(
        ConcurrentBag<string> logLines,
        List<string> newGlossaryStrings,
        List<Regex> compiledBadRegexes,
        TranslationSplit split,
        TextFileToSplit textFile,
        LlmConfig config,
        Regex chineseCharRegex,
        StringTokenReplacer tokenReplacer)
    {
        if (!split.SafeToTranslate)
            return false;

        if (TryHandleGameObjectReference(split, textFile) is bool gameObjResult)
            return gameObjResult;

        var preparedRaw = LineValidation.PrepareRaw(split.Text, tokenReplacer);
        var cleanedRaw = LineValidation.CleanupLineBeforeSaving(split.Text, split.Text, textFile, tokenReplacer);
        var preparedResultRaw = LineValidation.CleanupLineBeforeSaving(preparedRaw, preparedRaw, textFile, tokenReplacer);

        if (TryHandleAlreadyTranslated(logLines, split, textFile, preparedRaw, cleanedRaw, preparedResultRaw, chineseCharRegex) is bool alreadyTranslatedResult)
            return alreadyTranslatedResult;

        if (TryFlagForNewGlossary(logLines, newGlossaryStrings, split, textFile, preparedRaw))
            return true;

        if (TryFlagForBadRegex(logLines, compiledBadRegexes, split, textFile))
            return true;

        if (TryHandleDynamicStringExclusion(split, textFile))
            return true;

        if (TryApplyManualTranslation(logLines, config, split, textFile, preparedRaw) is bool manualResult)
            return manualResult;

        if (TryFlagEmptyTranslation(split, preparedRaw))
            return true;

        return ApplyTranslationRules(logLines, config, split, textFile, preparedRaw);
    }

    private static bool? TryHandleGameObjectReference(TranslationSplit split, TextFileToSplit textFile)
    {
        if (textFile.TextFileType != TextFileType.LocalTextString)
            return null;

        if (!TranslationService.IsGameObjectReference(split.Text))
            return null;

        if (split.Text != split.Translated)
        {
            split.Translated = split.Text;
            split.ResetFlags();
            return true;
        }

        return false;
    }

    private static bool? TryHandleAlreadyTranslated(
        ConcurrentBag<string> logLines,
        TranslationSplit split,
        TextFileToSplit textFile,
        string preparedRaw,
        string cleanedRaw,
        string preparedResultRaw,
        Regex chineseCharRegex)
    {
        if (chineseCharRegex.IsMatch(preparedRaw))
            return null;

        if (split.Translated == cleanedRaw || split.Translated == preparedResultRaw)
            return null;

        logLines.Add($"Already Translated {textFile.Path} \n{split.Translated}");
        split.Translated = preparedResultRaw;
        split.ResetFlags();
        return true;
    }

    private static bool TryFlagForNewGlossary(
        ConcurrentBag<string> logLines,
        List<string> newGlossaryStrings,
        TranslationSplit split,
        TextFileToSplit textFile,
        string preparedRaw)
    {
        foreach (var glossary in newGlossaryStrings)
        {
            if (preparedRaw.Contains(glossary))
            {
                logLines.Add($"New Glossary {textFile.Path} Replaces: \n{split.Translated}");
                split.FlaggedForRetranslation = true;
                return true;
            }
        }

        return false;
    }

    private static bool TryFlagForBadRegex(
        ConcurrentBag<string> logLines,
        List<Regex> compiledBadRegexes,
        TranslationSplit split,
        TextFileToSplit textFile)
    {
        foreach (var badRegex in compiledBadRegexes)
        {
            if (badRegex.IsMatch(split.Text) || badRegex.IsMatch(split.Translated ?? string.Empty))
            {
                logLines.Add($"Bad Regex {textFile.Path} Replaces: \n{split.Translated}");
                split.FlaggedForRetranslation = true;
                return true;
            }
        }

        return false;
    }

    private static bool TryHandleDynamicStringExclusion(TranslationSplit split, TextFileToSplit textFile)
    {
        if (textFile.TextFileType != TextFileType.DynamicStrings)
            return false;

        if (split.Text.Contains("Sprite")
            || split.Text.Contains("UI")
            || split.Text.Contains("Prefab")
            || split.Text.StartsWith("INVALIDCHAR:"))
        {
            split.SafeToTranslate = false;
            return true;
        }

        return false;
    }

    private static bool? TryApplyManualTranslation(
        ConcurrentBag<string> logLines,
        LlmConfig config,
        TranslationSplit split,
        TextFileToSplit textFile,
        string preparedRaw)
    {
        if (!textFile.EnableGlossary)
            return null;

        foreach (var manual in config.Runtime.ManualTranslations)
        {
            if (split.Text != manual.Raw)
                continue;

            if (split.Translated != manual.Result)
            {
                logLines.Add($"Manually Translated {textFile.Path} \n{split.Text}\n{split.Translated}");
                split.Translated = LineValidation.CleanupLineBeforeSaving(LineValidation.PrepareResult(preparedRaw, manual.Result, textFile, split.Split), split.Text, textFile, new StringTokenReplacer());
                split.ResetFlags();
                return true;
            }

            return false;
        }

        return null;
    }

    private static bool TryFlagEmptyTranslation(TranslationSplit split, string preparedRaw)
    {
        if (string.IsNullOrEmpty(split.Translated) && !string.IsNullOrEmpty(preparedRaw))
        {
            split.FlaggedForRetranslation = true;
            split.FlaggedMistranslation = "Failed"; //Easy search
            return true;
        }

        return false;
    }

    private static bool ApplyTranslationRules(
        ConcurrentBag<string> logLines,
        LlmConfig config,
        TranslationSplit split,
        TextFileToSplit textFile,
        string preparedRaw)
    {
        bool modified = false;

        if (MatchesBadWords(split.Translated))
        {
            logLines.Add($"Matches Bad words ... {textFile.Path} Replaces: \n{split.Translated}");
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        // Glossary Clean up - this won't check our manual jobs
        modified = CheckMistranslationGlossary(config, split, modified, textFile);
        modified = CheckHallucinationGlossary(config, split, modified, textFile);

        var modelConfig = LlmHelpers.CalculateModelConfig(config, preparedRaw);

        // Characters
        if (preparedRaw.EndsWith("...")
            && preparedRaw.Length < 15
            && !split.Translated.EndsWith("...")
            && !split.Translated.EndsWith("...?")
            && !split.Translated.EndsWith("...!")
            && !split.Translated.EndsWith("...!!")
            && !split.Translated.EndsWith("...?!"))
        {
            logLines.Add($"Missing ... {textFile.Path} Replaces: \n{split.Translated}");
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (preparedRaw.StartsWith("...") && !split.Translated.StartsWith("..."))
        {
            logLines.Add($"Missing ... {textFile.Path} Replaces: \n{split.Translated}");
            split.Translated = $"...{split.Translated}";
            modified = true;
        }

        // Trim line
        if (split.Translated.Trim().Length != split.Translated.Length)
        {
            logLines.Add($"Needed Trimming:{textFile.Path} \n{split.Translated}");
            split.Translated = split.Translated.Trim();
            modified = true;
        }

        // Clean up Diacritics -- Use a new tokenizer because the translated isnt generated off the prep raw
        var cleanedUp = LineValidation.CleanupLineBeforeSaving(split.Translated, preparedRaw, textFile, new StringTokenReplacer());
        if (cleanedUp != split.Translated)
        {
            logLines.Add($"Cleaned up {textFile.Path} \n{split.Translated}\n{cleanedUp}");
            split.Translated = cleanedUp;
            modified = true;
        }

        // Remove Invalid ones -- Have to use pure raw because translated is untokenised
        var translated2 = StringTokenReplacer.CleanTranslatedForApplyRules(split.Translated);
        var result = LineValidation.CheckTransalationSuccessful(modelConfig, split.Text, translated2, textFile, split.Split);
        if (!result.Valid)
        {
            logLines.Add($"Invalid {textFile.Path} Failures:{result.CorrectionPrompt}\n{split.Translated}");
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        foreach (var token in config.ExtraStringTokenReplacers)
        {
            if (split.Text.Contains(token) && !split.Translated.Contains(token))
            {
                logLines.Add($"Invalid {textFile.Path} Failures:Missing '{token}'\n{split.Translated}");
                split.FlaggedForRetranslation = true;
                modified = true;
            }
        }

        return modified;
    }

    private static bool CheckMistranslationGlossary(LlmConfig config, TranslationSplit split, bool modified, TextFileToSplit textFile)
    {
        if (!textFile.EnableGlossary)
            return modified;

        var tokenReplacer = new StringTokenReplacer();
        var preparedRaw = LineValidation.PrepareRaw(split.Text, tokenReplacer);

        if (split.Translated == null)
            return modified;

        foreach (var item in config.Runtime.GlossaryLines)
        {
            if (!item.CheckForBadTranslation)
                continue;

            //Exclusions and Targetted Glossary
            if (item.OnlyOutputFiles.Count > 0 && !item.OnlyOutputFiles.Contains(textFile.Path))
                continue;
            else if (item.ExcludeOutputFiles.Count > 0 && item.ExcludeOutputFiles.Contains(textFile.Path))
                continue;

            if (preparedRaw.Contains(item.Raw) && !split.Translated.Contains(item.Result, StringComparison.OrdinalIgnoreCase))
            {
                var found = false;
                foreach (var alternative in item.AllowedAlternatives)
                {
                    found = split.Translated.Contains(alternative, StringComparison.OrdinalIgnoreCase);
                    if (found)
                        break;
                }

                if (!found)
                {
                    split.FlaggedForRetranslation = true;
                    split.FlaggedMistranslation += $"{item.Result},{item.Raw},";
                    modified = true;
                }
            }
        }

        return modified; // Will be previous value - even if it didnt find anything
    }

    private static bool CheckHallucinationGlossary(LlmConfig config, TranslationSplit split, bool modified, TextFileToSplit textFile)
    {
        if (!textFile.EnableGlossary)
            return modified;

        var tokenReplacer = new StringTokenReplacer();
        var preparedRaw = LineValidation.PrepareRaw(split.Text, tokenReplacer);

        if (split.Translated == null)
            return modified;

        foreach (var item in config.Runtime.GlossaryLines)
        {
            var wordPattern = $"\\b{item.Result}\\b";

            if (!preparedRaw.Contains(item.Raw) && split.Translated.Contains(item.Result))
            {
                if (!item.CheckForMisusedTranslation)
                    continue;

                //Exclusions and Targetted Glossary
                if (item.OnlyOutputFiles.Count > 0 && !item.OnlyOutputFiles.Contains(textFile.Path))
                    continue;
                else if (item.ExcludeOutputFiles.Count > 0 && item.ExcludeOutputFiles.Contains(textFile.Path))
                    continue;

                // Regex matches on terms with ... match incorrectly
                if (!Regex.IsMatch(split.Translated, wordPattern, RegexOptions.IgnoreCase))
                    continue;

                // Check for Alternatives
                var dupes = config.Runtime.GlossaryLines.Where(s => s.Result == item.Result && s.Raw != item.Raw);
                bool found = false;

                foreach (var dupe in dupes)
                {
                    found = preparedRaw.Contains(dupe.Raw);
                    if (found)
                        break;
                }

                if (!found)
                {
                    split.FlaggedForRetranslation = true;
                    split.FlaggedHallucination += $"{item.Result},{item.Raw},";
                    modified = true;
                }
            }
        }

        return modified; // Will be previous value - even if it didnt find anything
    }

    public static bool MatchesBadWords(string input)
    {
        HashSet<string> words =
        [
            "hiu", "tut", "thut", "oi", "avo", "porqe", "obrigado",
                "knight", "knights", "knight-at-arms", "knights-errant",
                "nom", "esto", "tem", "mais", "com", "ver", "nos", "sobre", "vermos",
                "dar", "nam", "J'ai", "je", "veux", "pas", "ele", "una", "keqi", "shiwu",
                "ich", "ein", "der", "ganzes", "Leben", "dort", //"de", NAmes can have de
                "knight", "thay", "tien", "div", "html", "tiantu", "ngoc", "truong", "Phong"
        ];

        string pattern = $@"\b({string.Join("|", words)})\b";

        return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase);
    }


    public static async Task ResetAllFlags(string workingDirectory, TextFileToSplit[] textFiles)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory);
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory,
            textFiles,
            async (outputFile, textFileToTranslate, fileLines) =>
        {
            foreach (var line in fileLines)
                foreach (var split in line.Splits)
                    // Reset all the retrans flags
                    split.ResetFlags(false);

            await File.WriteAllTextAsync(outputFile, serializer.Serialize(fileLines));
        });
    }

    public static async Task SetSplitAsInvalid(string workingDirectory,
        TextFileToSplit[] textFiles,
        List<string> badStrings)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory);
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory,
            textFiles,
            async (outputFile, textFileToTranslate, fileLines) =>
        {
            var recordsModded = 0;

            foreach (var line in fileLines)
                foreach (var split in line.Splits)
                {
                    if (badStrings.Any(s => split.Text.Contains(s)))
                    {
                        split.FlaggedForRetranslation = true;
                        split.FlaggedMistranslation = "Bad Character";
                        recordsModded++;
                    }
                }

            await File.WriteAllTextAsync(outputFile, serializer.Serialize(fileLines));
            Console.WriteLine($"Writing {recordsModded} records to {outputFile}");
        });
    }

    public static async Task SetSplitAsInvalidByRegex(string workingDirectory,
        TextFileToSplit[] textFiles,
        List<string> badPatterns)
    {
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory,
            textFiles,
            async (outputFile, textFileToTranslate, fileLines) =>
        {
            var recordsModded = 0;

            foreach (var line in fileLines)
                foreach (var split in line.Splits)
                {
                    if (badPatterns.Any(p => Regex.IsMatch(split.Text, p)))
                    {
                        split.FlaggedForRetranslation = true;
                        split.FlaggedMistranslation = "Bad Character";
                        recordsModded++;
                    }
                }

            await File.WriteAllTextAsync(outputFile, serializer.Serialize(fileLines));
            Console.WriteLine($"Writing {recordsModded} records to {outputFile}");
        });
    }

    public static async Task CleanUpSomeRegexes(string workingDirectory,
        TextFileToSplit[] textFiles,
        List<(string pattern, string replacement)> regex)
    {
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory,
            textFiles,
            async (outputFile, textFileToTranslate, fileLines) =>
        {
            var recordsModded = 0;

            foreach (var line in fileLines)
                foreach (var split in line.Splits)
                {

                    // Replace using pattern and replacement
                    if (regex.Any(r => Regex.IsMatch(split.Translated, r.pattern)))
                    {
                        var original = split.Text;
                        foreach (var (pattern, replacement) in regex)
                        {
                            split.Translated = Regex.Replace(split.Translated, pattern, replacement);
                            recordsModded++;
                        }
                    }
                }

            await File.WriteAllTextAsync(outputFile, serializer.Serialize(fileLines));
            Console.WriteLine($"Writing {recordsModded} records to {outputFile}");
        });
    }
}
