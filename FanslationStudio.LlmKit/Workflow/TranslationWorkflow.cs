using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Workflow;

public static class TranslationWorkflow
{
    public static async Task ApplyAllRulesToCurrentTranslation(string workingDirectory, TextFileToSplit[] textFiles, GameHooks? hooks = null)
    {
        await UpdateCurrentTranslationLines(workingDirectory, true, textFiles, hooks);
    }

    public static async Task TranslateLines(string workingDirectory, TextFileToSplit[] textFiles, GameHooks? hooks = null)
    {
        await PerformTranslateLines(workingDirectory, false, textFiles, hooks);
    }

    public static async Task TranslateLinesBruteForce(string workingDirectory, TextFileToSplit[] textFiles, GameHooks? hooks = null)
    {
        await PerformTranslateLines(workingDirectory, true, textFiles, hooks);
    }

    private static async Task PerformTranslateLines(string workingDirectory, bool keepCleaning, TextFileToSplit[] textFileToSplits, GameHooks? hooks)
    {
        if (!keepCleaning)
        {
            await TranslationService.TranslateViaLlmAsync(workingDirectory, false, textFileToSplits, hooks);
            return;
        }

        PrintSeparator();
        int remaining = await UpdateCurrentTranslationLines(workingDirectory, false, textFileToSplits, hooks);
        PrintSeparator();

        int iterations = 0;
        while (remaining > 0 && iterations < 30)
        {
            await TranslationService.TranslateViaLlmAsync(workingDirectory, false, textFileToSplits, hooks);
            PrintSeparator();
            remaining = await UpdateCurrentTranslationLines(workingDirectory, false, textFileToSplits, hooks);
            PrintSeparator();
            iterations++;
        }
    }

    private static void PrintSeparator()
    {
        Console.WriteLine("-------------------------------------------------------------------");
        Console.WriteLine("-------------------------------------------------------------------");
    }

    private static async Task<int> UpdateCurrentTranslationLines(string workingDirectory, bool resetFlag, TextFileToSplit[] textFileToSplits, GameHooks? hooks)
    {
        var context = BuildTranslationRuleContext(workingDirectory, hooks);
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
        Regex ChineseCharRegex);

    private static TranslationRuleContext BuildTranslationRuleContext(string workingDirectory, GameHooks? hooks)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory, hooks);
        var chineseCharRegex = new Regex(LineValidation.ChineseCharPattern, RegexOptions.Compiled);

        return new TranslationRuleContext(config, chineseCharRegex);
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
        bool isFullFileRetrans = false;

        Parallel.ForEach(fileLines, line =>
        {
            int lineModded = ProcessLine(line, textFile, isFullFileRetrans, resetFlag, logLines, context);
            Interlocked.Add(ref recordsModded, lineModded);
        });

        if (recordsModded > 0 || resetFlag)
        {
            Console.WriteLine($"Writing {recordsModded} records to {outputFile}");
            var serializer = YamlHelper.CreateSerializer();
            await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
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

            if (UpdateSplit(logLines, split, textFile, context.Config, context.ChineseCharRegex, tokenReplacer))
                modded++;
        }

        return modded;
    }

    public static bool UpdateSplit(
        ConcurrentBag<string> logLines,
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

        if (TryHandleDynamicStringExclusion(split, textFile))
            return true;

        if (TryApplyManualTranslation(logLines, config, split, textFile, preparedRaw))
            return true;

        if (TryFlagEmptyTranslation(split, preparedRaw))
            return true;

        if (TryApplyGameSpecificRepair(logLines, split, textFile, config) is bool gameSpecificResult)
            return gameSpecificResult;

        if (TryFlagAllCapsTranslation(split, preparedRaw))
            return true;

        return ApplyTranslationRules(logLines, config, split, textFile, preparedRaw);
    }

    /// <summary>
    /// Re-runs <see cref="Configuration.GameHooks.CustomPostRepair"/>/<see cref="Configuration.GameHooks.CustomColumnRepair"/>
    /// against an *already-translated* split's existing <see cref="TranslationSplit.Translated"/>
    /// value, and falls back to <see cref="Configuration.GameHooks.CustomColumnValidator"/> when the repair
    /// makes no change. These hooks otherwise only run during a live LLM call (inside
    /// <see cref="LineValidation.PrepareResult"/>/<see cref="LineValidation.CheckTransalationSuccessful"/>,
    /// called from <c>TranslationService</c>), so a deterministic fix added to a game-specific hook
    /// (e.g. stripping braces an LLM wrapped around a placeholder token) would otherwise never reach
    /// text translated in an earlier pass and already sitting in <c>Files/Converted</c> - this lets
    /// <see cref="ApplyAllRulesToCurrentTranslation"/> retroactively apply/detect the same rules
    /// without paying for a full retranslation. Returns null when there is nothing to check (no
    /// existing translation yet) or when both the repair and the validator find nothing wrong.
    ///
    /// Deliberately passes <see cref="TranslationSplit.Text"/> (the untouched raw), not a freshly
    /// computed <c>preparedRaw</c>, to the game-specific hooks - <c>preparedRaw</c> goes through
    /// <see cref="StringTokenReplacer.Replace"/>, whose <c>NumericValueRegex</c> swaps out any bare
    /// digit (e.g. the "0" in "#PlotTargetInteractName0#") for an internal "{n}" sentinel, which
    /// makes a game's own "#...#"-shaped placeholder regex unable to match it in the raw side at
    /// all. That's harmless during a live LLM call because both sides of the comparison (raw and
    /// the not-yet-restored llmResult) get mangled identically, but here <see cref="TranslationSplit.Translated"/>
    /// is already the final, fully-restored text from an earlier run - comparing it against a
    /// mangled raw produced false "token count" mismatches (a real, correctly preserved token looked
    /// like it had been added out of nowhere, since the raw side's count came up zero).
    /// </summary>
    private static bool? TryApplyGameSpecificRepair(
        ConcurrentBag<string> logLines,
        TranslationSplit split,
        TextFileToSplit textFile,
        LlmConfig config)
    {
        if (string.IsNullOrEmpty(split.Translated))
            return null;

        var raw = split.Text;

        var repaired = LineValidation.PrepareResult(raw, split.Translated, config.Hooks, textFile, split.Split);
        if (repaired != split.Translated)
        {
            logLines.Add($"Game-specific repair {textFile.Path} \n{split.Translated}\n->\n{repaired}");
            split.Translated = repaired;
            split.ResetFlags();
            return true;
        }

        if (config.Hooks?.CustomColumnValidator != null)
        {
            var failureReason = config.Hooks.CustomColumnValidator(textFile, split.Split, raw, split.Translated);
            if (failureReason != null)
            {
                logLines.Add($"Game-specific validation failed {textFile.Path} ({failureReason}) \n{split.Translated}");
                split.FlaggedForRetranslation = true;
                split.FlaggedMistranslation = failureReason;
                return true;
            }
        }

        return null;
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

    private static bool TryApplyManualTranslation(
        ConcurrentBag<string> logLines,
        LlmConfig config,
        TranslationSplit split,
        TextFileToSplit textFile,
        string preparedRaw)
    {
        if (!textFile.EnableGlossary)
            return false;

        foreach (var manual in config.Runtime.ManualTranslations)
        {
            if (split.Text != manual.Raw)
                continue;

            if (split.Translated != manual.Result)
            {
                logLines.Add($"Manually Translated {textFile.Path} \n{split.Text}\n{split.Translated}");
                split.Translated = LineValidation.CleanupLineBeforeSaving(LineValidation.PrepareResult(preparedRaw, manual.Result, config.Hooks, textFile, split.Split), split.Text, textFile, new StringTokenReplacer());
                split.ResetFlags();
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryFlagAllCapsTranslation(TranslationSplit split, string preparedRaw)
    {
        // things like "I..." flag this and its annoying
        //if (!string.IsNullOrEmpty(split.Translated) 
        //    && split.Translated.Length > 1
        //    && split.Translated.ToUpper() == split.Translated)
        //{
        //    split.FlaggedForRetranslation = true;
        //    split.FlaggedMistranslation = "All caps";
        //    return true;
        //}

        return false;
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
        var modelConfig = LlmHelpers.CalculateModelConfig(config, preparedRaw);

        // Characters
        if (IsMissingRequiredEllipsis(preparedRaw, split.Translated))
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

        // A raw split starting with a comma ("，" or plain ",") is a fragment continuing directly
        // from the previous cell/sentence (e.g. a compound field split by
        // CompoundFieldSplitter) - the translation must keep that comma as the very first
        // characters too. The model routinely relocates it into a natural-sounding construction
        // instead (raw "，比方说这太祖长拳，" -> "For example, this Taijiquan" instead of
        // ", for example, this Taijiquan"), silently losing the fragment-boundary marker.
        if (preparedRaw.StartsWith('，') || preparedRaw.StartsWith(','))
        {
            if (split.Translated.StartsWith(',') && !split.Translated.StartsWith(", "))
            {
                // Comma preserved but glued directly onto the next word with no space.
                logLines.Add($"Leading comma missing space {textFile.Path} Replaces: \n{split.Translated}");
                split.Translated = $", {split.Translated[1..].TrimStart()}";
                modified = true;
            }
            else if (!split.Translated.StartsWith(", ") && !split.Translated.StartsWith('，'))
            {
                // Comma dropped/relocated entirely.
                logLines.Add($"Missing leading comma {textFile.Path} Replaces: \n{split.Translated}");
                split.Translated = $", {split.Translated}";
                modified = true;
            }
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

        // Single shared rule list (see EvaluateRules) - covers bad words, glossary mistranslation/
        // hallucination, missing ellipsis, missing required token, any game-specific
        // CustomColumnValidator, and the generic structural check. A new rule added there applies
        // here and to QualityReviewWorkflow's QC gate/rule-check without anything more to edit.
        var translated2 = StringTokenReplacer.CleanTranslatedForApplyRules(split.Translated);
        var ruleResult = EvaluateRules(config, modelConfig, preparedRaw, split.Text, translated2, textFile, split.Split);

        if (ruleResult.MistranslatedGlossaryTerms.Count > 0)
        {
            foreach (var item in ruleResult.MistranslatedGlossaryTerms)
                split.FlaggedMistranslation += $"{item.Result},{item.Raw},";
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.HallucinationReason != null)
        {
            split.FlaggedHallucination += ruleResult.HallucinationReason;
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.BadWordsReason != null)
        {
            logLines.Add($"Matches Bad words ... {textFile.Path} Replaces: \n{split.Translated}");
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.EllipsisReason != null)
        {
            logLines.Add($"Missing ... {textFile.Path} Replaces: \n{split.Translated}");
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.MissingTokenReason != null)
        {
            logLines.Add($"Invalid {textFile.Path} Failures:{ruleResult.MissingTokenReason}\n{split.Translated}");
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.CustomValidatorReason != null)
        {
            logLines.Add($"Invalid {textFile.Path} Failures:{ruleResult.CustomValidatorReason}\n{split.Translated}");
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.StructuralReason != null)
        {
            logLines.Add($"Invalid {textFile.Path} Failures:{ruleResult.StructuralReason}\n{split.Translated}");
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        return modified;
    }

    /// <summary>
    /// True when <paramref name="preparedRaw"/> ends in an ellipsis that <paramref name="translated"/>
    /// fails to preserve - shared between <see cref="ApplyTranslationRules"/> (the main
    /// translate/retranslate pipeline) and <see cref="Workflow.QualityReviewWorkflow"/>'s QC
    /// validation gate, so a QC-proposed correction is held to the same bar as a normal translation
    /// attempt instead of silently allowing what would otherwise trigger a retranslation.
    /// </summary>
    internal static bool IsMissingRequiredEllipsis(string preparedRaw, string translated)
    {
        // Some fragments legitimately start with a stray closing quote mark ("”"/"'"/"\"") split
        // off a larger quoted sentence whose opening quote lives in the surrounding template
        // literal (e.g. "“⟦0⟧{0}" + fragment "”竟有这等境界...") - the LLM correctly renders that
        // closing quote as a trailing apostrophe/quote AFTER the ellipsis ("...'"), which a plain
        // EndsWith("...") check doesn't recognize, incorrectly flagging a fine translation as
        // having dropped the ellipsis. Strip any trailing quote-like characters before comparing.
        var translatedForEllipsisCheck = translated.TrimEnd('\'', '"', '’', '‘', '”', '“');

        return preparedRaw.EndsWith("...")
            && preparedRaw.Length < 15
            && !translatedForEllipsisCheck.EndsWith("...")
            && !translatedForEllipsisCheck.EndsWith("...?")
            && !translatedForEllipsisCheck.EndsWith("...!")
            && !translatedForEllipsisCheck.EndsWith("...!!")
            && !translatedForEllipsisCheck.EndsWith("...?!");
    }

    /// <summary>
    /// Returns the first configured <see cref="LlmConfig.ExtraStringTokenReplacers"/> token present
    /// in <paramref name="raw"/> but missing from <paramref name="translated"/>, or null if every
    /// such token that appears in <paramref name="raw"/> was preserved. Shared between
    /// <see cref="ApplyTranslationRules"/> and <see cref="Workflow.QualityReviewWorkflow"/>'s QC
    /// validation gate - see <see cref="IsMissingRequiredEllipsis"/>'s doc comment for why.
    /// </summary>
    internal static string? FindMissingRequiredToken(string raw, string translated, IEnumerable<string> extraTokens)
    {
        foreach (var token in extraTokens)
        {
            if (raw.Contains(token) && !translated.Contains(token))
                return token;
        }

        return null;
    }

    /// <summary>
    /// The full set of hard-fail rule checks a candidate translation must pass, computed once and
    /// shared by every caller: a normal translation attempt's post-LLM rule pass
    /// (<see cref="ApplyTranslationRules"/>), a freshly proposed QC correction's accept-time gate,
    /// and QC's retroactive re-check of an already-accepted <see cref="TranslationSplit.QcTranslated"/>
    /// (both in <see cref="Workflow.QualityReviewWorkflow"/>). Adding a brand new kind of check
    /// belongs here, once - every caller reading <see cref="RuleCheckResult.AllReasons"/> (or a
    /// specific field, for a category it wants to report distinctly - see
    /// <see cref="TranslationSplit.FlaggedMistranslation"/>/<see cref="TranslationSplit.FlaggedHallucination"/>)
    /// picks it up automatically, with nowhere else that needs editing.
    ///
    /// Takes two raw-text variants because the checks folded in here were never consistent about
    /// which one they used before this consolidation, and preserving that (rather than silently
    /// changing a live pipeline's behavior) matters more than tidiness: <paramref name="preparedRaw"/>
    /// feeds the glossary/ellipsis checks and is meant to be <see cref="LineValidation.PrepareRaw"/>'s
    /// output, exactly like the <c>Translated</c> path's own "preparedRaw" local variable it's named
    /// after; <paramref name="splitRaw"/> feeds <see cref="LineValidation.CheckTransalationSuccessful"/>/
    /// <see cref="FindMissingRequiredToken"/>/<see cref="Configuration.GameHooks.CustomColumnValidator"/> and
    /// is meant to be the split's untouched raw <see cref="TranslationSplit.Text"/>. QC (which has no
    /// split of its own for a templated/reconstructed cell) calls <c>PrepareRaw(rawText, null)</c> for
    /// its <paramref name="preparedRaw"/> argument - the CJK-punctuation-normalized half only, e.g.
    /// the CJK ellipsis glyph "…" -> "..." so <see cref="IsMissingRequiredEllipsis"/> can actually
    /// match it - deliberately passing a null <see cref="StringTokenReplacer"/> so it skips the
    /// token-masking half, which would otherwise corrupt the column's own replacer (already
    /// populated for masking the LLM prompt/restoring its response). <paramref name="splitRaw"/>
    /// stays QC's untouched raw column text either way.
    /// </summary>
    internal static RuleCheckResult EvaluateRules(
        LlmConfig config,
        ModelExecutionConfig modelConfig,
        string preparedRaw,
        string splitRaw,
        string candidate,
        TextFileToSplit textFile,
        int? column)
    {
        var mistranslatedGlossaryTerms = FindGlossaryMistranslations(config, preparedRaw, candidate, textFile).ToList();
        var hallucinationReason = FindGlossaryHallucination(preparedRaw, candidate, config, textFile);
        var badWordsReason = MatchesBadWords(candidate) ? "Matches the bad-words list." : null;
        var ellipsisReason = IsMissingRequiredEllipsis(preparedRaw, candidate) ? "Missing an ellipsis '...' required by the source." : null;
        var missingTokenReason = FindMissingRequiredToken(splitRaw, candidate, config.ExtraStringTokenReplacers) is string missingToken
            ? $"Missing required token '{missingToken}'."
            : null;
        var customValidatorReason = config.Hooks?.CustomColumnValidator?.Invoke(textFile, column, splitRaw, candidate);
        var validation = LineValidation.CheckTransalationSuccessful(modelConfig, splitRaw, candidate, textFile, config.Hooks, column);
        var structuralReason = validation.Valid ? null : validation.CorrectionPrompt;

        return new RuleCheckResult(
            mistranslatedGlossaryTerms,
            hallucinationReason,
            badWordsReason,
            ellipsisReason,
            missingTokenReason,
            customValidatorReason,
            structuralReason);
    }

    /// <summary>See <see cref="EvaluateRules"/>.</summary>
    internal sealed record RuleCheckResult(
        List<GlossaryLine> MistranslatedGlossaryTerms,
        string? HallucinationReason,
        string? BadWordsReason,
        string? EllipsisReason,
        string? MissingTokenReason,
        string? CustomValidatorReason,
        string? StructuralReason)
    {
        /// <summary>
        /// Every failure reason in generic order - for a caller (like QC) that just wants "did
        /// anything fail, and why" without breaking a category out into its own dedicated field.
        /// </summary>
        public IEnumerable<string> AllReasons
        {
            get
            {
                foreach (var item in MistranslatedGlossaryTerms)
                    yield return $"Glossary term '{item.Raw}' (expected '{item.Result}') is missing.";

                if (HallucinationReason != null) yield return HallucinationReason;
                if (BadWordsReason != null) yield return BadWordsReason;
                if (EllipsisReason != null) yield return EllipsisReason;
                if (MissingTokenReason != null) yield return MissingTokenReason;
                if (CustomValidatorReason != null) yield return CustomValidatorReason;
                if (StructuralReason != null) yield return StructuralReason;
            }
        }

        public bool HasFailure =>
            MistranslatedGlossaryTerms.Count > 0
            || HallucinationReason != null
            || BadWordsReason != null
            || EllipsisReason != null
            || MissingTokenReason != null
            || CustomValidatorReason != null
            || StructuralReason != null;
    }

    /// <summary>
    /// Pure glossary-mistranslation detector shared (via <see cref="EvaluateRules"/>) by the normal
    /// translation pipeline's rule check and <see cref="Workflow.QualityReviewWorkflow"/>'s QC
    /// gate/rule-check - a single implementation so a change to what counts as "this glossary term
    /// was mistranslated" only has to be made once instead of drifting between two pipelines. Returns
    /// every glossary line whose Raw/RawSimplified/RawTraditional matched in <paramref name="rawText"/>
    /// but whose Result (or an allowed alternative) is missing from <paramref name="candidate"/> -
    /// empty if none. Mirrors <see cref="IsGlossaryHallucination"/>'s matching approach (all three raw
    /// variants), which the older, QC-only version of this check
    /// (<c>QualityReviewWorkflow.CheckGlossaryDrift</c>) already did but this one, checking only
    /// <see cref="GlossaryLine.Raw"/>, did not - unifying picks up that stricter behavior for both
    /// pipelines rather than the other way round.
    /// </summary>
    internal static IEnumerable<GlossaryLine> FindGlossaryMistranslations(LlmConfig config, string rawText, string candidate, TextFileToSplit textFile)
    {
        if (!textFile.EnableGlossary)
            yield break;

        foreach (var item in config.Runtime.GlossaryLines)
        {
            if (!item.CheckForBadTranslation)
                continue;

            //Exclusions and Targetted Glossary
            if (item.OnlyOutputFiles.Count > 0 && !item.OnlyOutputFiles.Contains(textFile.Path))
                continue;
            else if (item.ExcludeOutputFiles.Count > 0 && item.ExcludeOutputFiles.Contains(textFile.Path))
                continue;

            var matchedRaw = (!string.IsNullOrEmpty(item.Raw) && rawText.Contains(item.Raw))
                || (!string.IsNullOrEmpty(item.RawSimplified) && rawText.Contains(item.RawSimplified))
                || (!string.IsNullOrEmpty(item.RawTraditional) && rawText.Contains(item.RawTraditional));

            if (!matchedRaw || candidate.Contains(item.Result, StringComparison.OrdinalIgnoreCase))
                continue;

            if (item.AllowedAlternatives.Any(alternative => candidate.Contains(alternative, StringComparison.OrdinalIgnoreCase)))
                continue;

            yield return item;
        }
    }

    /// <summary>
    /// True when <paramref name="translated"/> contains <paramref name="item"/>'s Result even
    /// though its Raw (the term it's supposed to translate) never appeared in <paramref name="preparedRaw"/>
    /// at all - i.e. the model appears to have hallucinated a glossary-mapped term into text that
    /// never asked for it - and no other glossary line mapping to the same Result is present in
    /// <paramref name="preparedRaw"/> to explain the occurrence.
    /// </summary>
    private static bool IsGlossaryHallucination(GlossaryLine item, List<GlossaryLine> allGlossaryLines, string preparedRaw, string translated, TextFileToSplit textFile)
    {
        if (preparedRaw.Contains(item.Raw) || !translated.Contains(item.Result))
            return false;

        if (!item.CheckForMisusedTranslation)
            return false;

        //Exclusions and Targetted Glossary
        if (item.OnlyOutputFiles.Count > 0 && !item.OnlyOutputFiles.Contains(textFile.Path))
            return false;
        else if (item.ExcludeOutputFiles.Count > 0 && item.ExcludeOutputFiles.Contains(textFile.Path))
            return false;

        // Regex matches on terms with ... match incorrectly
        var wordPattern = $"\\b{item.Result}\\b";
        if (!Regex.IsMatch(translated, wordPattern, RegexOptions.IgnoreCase))
            return false;

        // Check for Alternatives
        var dupes = allGlossaryLines.Where(s => s.Result == item.Result && s.Raw != item.Raw);
        foreach (var dupe in dupes)
        {
            if (preparedRaw.Contains(dupe.Raw))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Pure hallucination detector shared by <see cref="EvaluateRules"/> (used by both the
    /// <c>Translated</c> pipeline and QC) - returns a description of the first hallucinated
    /// glossary term found, or null if none.
    /// </summary>
    internal static string? FindGlossaryHallucination(string preparedRaw, string translated, LlmConfig config, TextFileToSplit textFile)
    {
        if (!textFile.EnableGlossary)
            return null;

        foreach (var item in config.Runtime.GlossaryLines)
        {
            if (IsGlossaryHallucination(item, config.Runtime.GlossaryLines, preparedRaw, translated, textFile))
                return $"Hallucinated glossary term '{item.Result}' (maps from '{item.Raw}', which is not present in the source).";
        }

        return null;
    }

    public static bool MatchesBadWords(string input)
    {
        HashSet<string> words =
        [
            //"hiu", "tut", "thut", "oi", "avo", "porqe", "obrigado",
                "knight", "knight", "knights", "knight-at-arms", "knights-errant",
                //"nom", "esto", "tem", "mais", "com", "ver", "nos", "sobre", "vermos",
                //"dar", "nam", "J'ai", "je", "veux", "pas", "ele", "una", "keqi", "shiwu",
                //"ich", "ein", "der", "ganzes", "Leben", "dort", //"de", NAmes can have de
                //"thay", "tien", "div", "html", "tiantu", "ngoc", "truong", "Phong"
        ];

        string pattern = $@"\b({string.Join("|", words)})\b";

        return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase);
    }


    public static async Task ResetAllFlags(string workingDirectory, TextFileToSplit[] textFiles)
    {
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory,
            textFiles,
            async (outputFile, textFileToTranslate, fileLines) =>
        {
            foreach (var line in fileLines)
                foreach (var split in line.Splits)
                    // Reset all the retrans flags
                    split.ResetFlags(false);

            await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
        });
    }

    public static async Task SetSplitAsInvalid(string workingDirectory,
        TextFileToSplit[] textFiles,
        List<string> badStrings)
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
                    if (badStrings.Any(s => split.Text.Contains(s)))
                    {
                        split.FlaggedForRetranslation = true;
                        split.FlaggedMistranslation = "Bad Character";
                        recordsModded++;
                    }
                }

            await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
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

            await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
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

            await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
            Console.WriteLine($"Writing {recordsModded} records to {outputFile}");
        });
    }
}
