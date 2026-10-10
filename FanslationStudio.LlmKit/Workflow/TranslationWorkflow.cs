using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Workflow;

public static class TranslationWorkflow
{
    public static async Task ApplyAllRulesToCurrentTranslation(string workingDirectory, TextFileToSplit[] textFiles, GameHooks? hooks = null)
    {
        await UpdateCurrentTranslationLines(workingDirectory, true, textFiles, hooks);

        // Catches columns left with a stale Qc* verdict from before UpdateSplit (above) eagerly
        // reset the anchor on every Translated change - e.g. a corpus translated/QC'd under an
        // older build. See QualityControlWorkflow.ResetStaleQcState's doc comment.
        await QualityControlWorkflow.ResetStaleQcState(workingDirectory, textFiles, hooks);
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

        // Config and the deserialized corpus are loaded once and reused by every translate/clean
        // pass below; each pass writes only the files it changed.
        var context = BuildTranslationRuleContext(workingDirectory, hooks);
        var corpus = TranslationCorpus.Load(workingDirectory, textFileToSplits, copyMissingFromExport: false);

        PrintSeparator();
        int remaining = await UpdateCurrentTranslationLines(workingDirectory, corpus, false, context);
        PrintSeparator();

        int iterations = 0;
        while (remaining > 0 && iterations < 30)
        {
            await TranslationService.TranslateViaLlmAsync(context.Config, corpus, false);
            PrintSeparator();
            remaining = await UpdateCurrentTranslationLines(workingDirectory, corpus, false, context);
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
        var corpus = TranslationCorpus.Load(workingDirectory, textFileToSplits, copyMissingFromExport: false);
        return await UpdateCurrentTranslationLines(workingDirectory, corpus, resetFlag, BuildTranslationRuleContext(workingDirectory, hooks));
    }

    private static async Task<int> UpdateCurrentTranslationLines(string workingDirectory, TranslationCorpus corpus, bool resetFlag, TranslationRuleContext context)
    {
        var totalRecordsModded = 0;
        var logLines = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(corpus.Files, new ParallelOptions { MaxDegreeOfParallelism = FileIteration.MaxParallelFiles }, async (file, _) =>
        {
            int recordsModded = await ProcessFileAsync(file.OutputFile, file.TextFile, file.Lines, resetFlag, logLines, context);
            Interlocked.Add(ref totalRecordsModded, recordsModded);
        });

        Console.WriteLine($"Total Lines: {totalRecordsModded} records");
        await File.WriteAllLinesAsync($"{workingDirectory}/TestResults/LineValidationLog.txt", logLines);

        return totalRecordsModded;
    }

    private record TranslationRuleContext(
        LlmConfig Config,
        Regex ChineseCharRegex);

    /// <summary>One file's input to the pronoun check: its line contexts.</summary>
    internal sealed record PronounRulesState(IReadOnlyDictionary<TranslationSplit, LineContext> Contexts);

    private static TranslationRuleContext BuildTranslationRuleContext(string workingDirectory, GameHooks? hooks)
    {
        var config = ConfigurationExtensions.GetConfiguration(workingDirectory, hooks);
        return new TranslationRuleContext(config, LineValidation.ChineseCharPatternCompiled);
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

        // Built once per file (the provider needs the whole file to carry a speaker across rows); a no-op when the
        // check is off.
        PronounRulesState? pronouns = null;
        if (context.Config.PronounCheck.Enabled)
        {
            var contexts = context.Config.Hooks?.LineContextProvider?.Invoke(context.Config.Runtime.WorkingDirectory ?? string.Empty, textFile, fileLines)
                ?? new Dictionary<TranslationSplit, LineContext>();
            pronouns = new PronounRulesState(contexts);
        }

        Parallel.ForEach(fileLines, line =>
        {
            int lineModded = ProcessLine(line, textFile, resetFlag, logLines, context, pronouns);
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
        bool resetFlag,
        ConcurrentBag<string> logLines,
        TranslationRuleContext context,
        PronounRulesState? pronouns = null)
    {
        var tokenReplacer = new StringTokenReplacer();
        int modded = 0;

        foreach (var split in line.Splits)
        {
            if (resetFlag)
                split.ResetFlags(false);

            if (UpdateSplit(logLines, line, split, textFile, context.Config, context.ChineseCharRegex, tokenReplacer, pronouns))
                modded++;
        }

        return modded;
    }

    public static bool UpdateSplit(
        ConcurrentBag<string> logLines,
        TranslationLine line,
        TranslationSplit split,
        TextFileToSplit textFile,
        LlmConfig config,
        Regex chineseCharRegex,
        StringTokenReplacer tokenReplacer) =>
        UpdateSplit(logLines, line, split, textFile, config, chineseCharRegex, tokenReplacer, null);

    private static bool UpdateSplit(
        ConcurrentBag<string> logLines,
        TranslationLine line,
        TranslationSplit split,
        TextFileToSplit textFile,
        LlmConfig config,
        Regex chineseCharRegex,
        StringTokenReplacer tokenReplacer,
        PronounRulesState? pronouns)
    {
        if (split.SafeToTranslate && config.Hooks?.CustomUnsafeToTranslateRule?.Invoke(textFile, line, split) == true)
        {
            split.SafeToTranslate = false;
            return true;
        }

        var translatedBeforeRules = split.Translated;
        var modified = UpdateSplitCore(logLines, split, textFile, config, chineseCharRegex, tokenReplacer, pronouns);

        if (!string.Equals(translatedBeforeRules, split.Translated, StringComparison.Ordinal))
            QualityControlHelpers.FindQcAnchor(line, split).ResetQcState();

        return modified;
    }

    private static bool UpdateSplitCore(
        ConcurrentBag<string> logLines,
        TranslationSplit split,
        TextFileToSplit textFile,
        LlmConfig config,
        Regex chineseCharRegex,
        StringTokenReplacer tokenReplacer,
        PronounRulesState? pronouns = null)
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

        return ApplyTranslationRules(logLines, config, split, textFile, preparedRaw)
            || TryFlagPronounDefect(logLines, split, textFile, config, pronouns);
    }

    /// <summary>
    /// Flags a translation with a pronoun defect (see <see cref="PronounDefectWorkflow"/>) for retranslation. Runs after
    /// every other rule found nothing. There is deliberately no once-per-run guard: a line the model cannot fix keeps
    /// being flagged, so the brute-force loop (bounded at 30 iterations) retranslating it over and over is the signal
    /// to add the line to the gold set and tune the prompt, and a later rules pass never disagrees with this one.
    /// </summary>
    internal static bool TryFlagPronounDefect(ConcurrentBag<string> logLines, TranslationSplit split, TextFileToSplit textFile, LlmConfig config, PronounRulesState? pronouns)
    {
        if (pronouns == null || split.FlaggedForRetranslation || split.Text.Length == 0 || split.Translated.Length == 0)
            return false;

        pronouns.Contexts.TryGetValue(split, out var lineContext);
        var category = PronounDefectWorkflow.Classify(split, config.Hooks, lineContext, config.PronounCheck.SkipWhenTranslationNamesSomeone).Category;
        if (category == null)
            return false;

        logLines.Add($"Pronoun defect ({category}) {textFile.Path} \n{split.Text}\n->\n{split.Translated}");
        split.FlaggedForRetranslation = true;
        split.FlaggedMistranslation = category;
        return true;
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
                split.FlaggedMistranslation = $"New glossary term {glossary}";
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
                split.FlaggedMistranslation = "Bad regex";
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

        if (!ManualTranslationsByRaw(config).TryGetValue(split.Text, out var manual))
            return false;

        if (split.Translated == manual.Result)
            return false;

        logLines.Add($"Manually Translated {textFile.Path} \n{split.Text}\n{split.Translated}");
        split.Translated = LineValidation.CleanupLineBeforeSaving(LineValidation.PrepareResult(preparedRaw, manual.Result, config.Hooks, textFile, split.Split), split.Text, textFile, new StringTokenReplacer());
        split.ResetFlags();
        return true;
    }

    private static readonly ConditionalWeakTable<List<GlossaryLine>, Dictionary<string, GlossaryLine>> ManualTranslationIndex = [];

    /// <summary>First manual translation per raw text, built once per loaded config.</summary>
    private static Dictionary<string, GlossaryLine> ManualTranslationsByRaw(LlmConfig config) =>
        ManualTranslationIndex.GetValue(config.Runtime.ManualTranslations, manuals =>
        {
            var index = new Dictionary<string, GlossaryLine>();
            foreach (var manual in manuals)
                index.TryAdd(manual.Raw, manual);
            return index;
        });

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
            AddFlagReason(split, "Missing ellipsis");
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
        // here and to QualityControlWorkflow's QC gate/rule-check without anything more to edit.
        var ruleResult = EvaluateRules(config, modelConfig, preparedRaw, split.Text, split.Translated, textFile, split.Split);

        if (ruleResult.MistranslatedGlossaryTerms.Count > 0)
        {
            logLines.Add($"Mistranslated Glossary {textFile.Path} Replaces: \n{split.Translated}");
            foreach (var item in ruleResult.MistranslatedGlossaryTerms)
                split.FlaggedMistranslation += $"{item.Result},{item.Raw},";
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.HallucinationReason != null)
        {
            logLines.Add($"Hallucination {textFile.Path} Replaces: \n{split.Translated}");
            split.FlaggedHallucination += ruleResult.HallucinationReason;
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.BadWordsReason != null)
        {
            logLines.Add($"Matches Bad words ... {textFile.Path} Replaces: \n{split.Translated}");
            AddFlagReason(split, "Bad words");
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.EllipsisReason != null)
        {
            logLines.Add($"Missing ... {textFile.Path} Replaces: \n{split.Translated}");
            AddFlagReason(split, "Missing ellipsis");
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.NegativeSignReason != null)
        {
            logLines.Add($"Missing negative sign {textFile.Path} Replaces: \n{split.Translated}");
            AddFlagReason(split, "Missing negative sign");
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.MissingTokenReason != null)
        {
            logLines.Add($"Invalid {textFile.Path} Failures:{ruleResult.MissingTokenReason}\n{split.Translated}");
            AddFlagReason(split, ruleResult.MissingTokenReason);
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.CustomValidatorReason != null)
        {
            logLines.Add($"Invalid {textFile.Path} Failures:{ruleResult.CustomValidatorReason}\n{split.Translated}");
            AddFlagReason(split, ruleResult.CustomValidatorReason);
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        if (ruleResult.StructuralReason != null)
        {
            logLines.Add($"Invalid {textFile.Path} Failures:{ruleResult.StructuralReason}\n{split.Translated}");
            AddFlagReason(split, ruleResult.StructuralReason);
            split.FlaggedForRetranslation = true;
            modified = true;
        }

        return modified;
    }

    /// <summary>
    /// Records why a split was flagged in <see cref="TranslationSplit.FlaggedMistranslation"/> (the glossary rule has its own
    /// "result,raw," form), so a flagged line in Converted can be diagnosed without finding the log it came from.
    /// A reason already recorded is kept and the new one appended.
    /// </summary>
    private static void AddFlagReason(TranslationSplit split, string reason)
    {
        var singleLine = reason.ReplaceLineEndings(" ").Trim();
        if (singleLine.Length == 0 || split.FlaggedMistranslation.Contains(singleLine, StringComparison.Ordinal))
            return;

        split.FlaggedMistranslation = split.FlaggedMistranslation.Length == 0 ? singleLine : $"{split.FlaggedMistranslation}; {singleLine}";
    }

    /// <summary>
    /// True when <paramref name="preparedRaw"/> ends in an ellipsis that <paramref name="translated"/>
    /// fails to preserve - shared between <see cref="ApplyTranslationRules"/> (the main
    /// translate/retranslate pipeline) and <see cref="Workflow.QualityControlWorkflow"/>'s QC
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
    /// Matches a hyphen-minus immediately before a digit, in either its raw ASCII form ("-5") or the
    /// non-breaking hyphen (U+2011) that translations saved before hyphens stopped being rewritten
    /// carry ("‑5") - both count as "the negative sign is still there" for <see cref="IsMissingRequiredNegativeSign"/>.
    /// </summary>
    private static readonly Regex NegativeNumberRegex = new(@"[-‑](?=\d)", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="translated"/> dropped a negative-number sign ("-0.5%" -> "0.5%")
    /// that <paramref name="preparedRaw"/> has - a real observed QC-correction quirk (see
    /// docs/features/translation-pipeline/quality-control-pass.md) where a stat/buff tooltip's leading "-" before a
    /// percentage got silently stripped while the rest of the line was accepted as a valid
    /// correction. Compares counts rather than exact positions since a fragment can legitimately
    /// reorder clauses around a number - what must never happen is the raw text having MORE
    /// negative-number signs than the candidate ends up with. Shared between
    /// <see cref="ApplyTranslationRules"/> and <see cref="Workflow.QualityControlWorkflow"/>'s QC
    /// validation gate, same as <see cref="IsMissingRequiredEllipsis"/>.
    /// </summary>
    internal static bool IsMissingRequiredNegativeSign(string preparedRaw, string translated)
    {
        var missing = NegativeNumberRegex.Matches(preparedRaw).Count - NegativeNumberRegex.Matches(translated).Count;
        if (missing <= 0)
            return false;

        // A sign can be carried by wording instead ("qi -10" -> "qi decreases by 10"); each such
        // word covers one dropped sign, so a genuinely stripped sign still fails.
        return missing > NegativeWordRegex.Matches(translated).Count;
    }

    private static readonly Regex NegativeWordRegex = new(
        @"\b(decreas\w*|reduc\w*|lower\w*|lose[sd]?|loss|minus|drop\w*|less|cut|penalt\w+|subtract\w*|declin\w*|deplet\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the first configured <see cref="LlmConfig.ExtraStringTokenReplacers"/> token present
    /// in <paramref name="raw"/> but missing from <paramref name="translated"/>, or null if every
    /// such token that appears in <paramref name="raw"/> was preserved. Shared between
    /// <see cref="ApplyTranslationRules"/> and <see cref="Workflow.QualityControlWorkflow"/>'s QC
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
    /// (both in <see cref="Workflow.QualityControlWorkflow"/>). Adding a brand new kind of check
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
        var badWordMatches = FindBadWordMatches(candidate);
        var badWordsReason = badWordMatches.Count > 0
            ? $"Matches the bad-words list (found: {string.Join(", ", badWordMatches)})."
            : null;
        var ellipsisReason = IsMissingRequiredEllipsis(preparedRaw, candidate) ? "Missing an ellipsis '...' required by the source." : null;
        var negativeSignReason = IsMissingRequiredNegativeSign(preparedRaw, candidate) ? "Missing a negative sign '-' before a number required by the source." : null;
        var missingTokenReason = FindMissingRequiredToken(splitRaw, candidate, config.ExtraStringTokenReplacers) is string missingToken
            ? $"Missing required token '{missingToken}'."
            : null;
        var customValidatorReason = config.Hooks?.CustomColumnValidator?.Invoke(textFile, column, splitRaw, candidate);
        var validation = LineValidation.CheckTransalationSuccessful(modelConfig, splitRaw, candidate, textFile, config.Hooks, column);
        var structuralReason = validation.Valid ? null
            : !string.IsNullOrWhiteSpace(validation.CorrectionPrompt) ? validation.CorrectionPrompt
            : validation.SilentFailures.Count > 0 ? string.Join(" ", validation.SilentFailures)
            : "Failed structural validation.";

        return new RuleCheckResult(
            mistranslatedGlossaryTerms,
            hallucinationReason,
            badWordsReason,
            ellipsisReason,
            negativeSignReason,
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
        string? NegativeSignReason,
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
                if (NegativeSignReason != null) yield return NegativeSignReason;
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
            || NegativeSignReason != null
            || MissingTokenReason != null
            || CustomValidatorReason != null
            || StructuralReason != null;
    }

    /// <summary>
    /// Pure glossary-mistranslation detector shared (via <see cref="EvaluateRules"/>) by the normal
    /// translation pipeline's rule check and <see cref="Workflow.QualityControlWorkflow"/>'s QC
    /// gate/rule-check - a single implementation so a change to what counts as "this glossary term
    /// was mistranslated" only has to be made once instead of drifting between two pipelines. Returns
    /// every glossary line whose Raw/RawSimplified/RawTraditional matched in <paramref name="rawText"/>
    /// but whose Result (or an allowed alternative) is missing from <paramref name="candidate"/> -
    /// empty if none. Mirrors <see cref="IsGlossaryHallucination"/>'s matching approach (all three raw
    /// variants), which the older, QC-only version of this check
    /// (<c>QualityControlWorkflow.CheckGlossaryDrift</c>) already did but this one, checking only
    /// <see cref="GlossaryLine.Raw"/>, did not - unifying picks up that stricter behavior for both
    /// pipelines rather than the other way round.
    /// </summary>
    internal static IEnumerable<GlossaryLine> FindGlossaryMistranslations(LlmConfig config, string rawText, string candidate, TextFileToSplit textFile)
    {
        if (!textFile.EnableGlossary)
            yield break;

        // Translations saved before hyphens stopped being rewritten still carry U+2011.
        candidate = candidate.Replace('\u2011', '-');

        // A term used only as part of a longer matched term (三七 inside 三七开) is not demanded on its own.
        var shadowed = GlossaryLine.FindShadowedByLongerMatch(rawText, config.Runtime.GlossaryLines, textFile.Path);

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

            if (!matchedRaw || shadowed.Contains(item) || candidate.Contains(item.Result, StringComparison.OrdinalIgnoreCase))
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
    private static readonly ConcurrentDictionary<string, Regex> WholeWordRegexCache = new();

    /// <summary>
    /// Case-insensitive whole-word matcher for a literal glossary term, cached per term. Lookarounds
    /// rather than <c>\b</c> so a term starting/ending in punctuation (e.g. "Elder (Retired)") still
    /// matches.
    /// </summary>
    private static Regex WholeWordRegex(string term) =>
        WholeWordRegexCache.GetOrAdd(term, t => new Regex($@"(?<!\w){Regex.Escape(t)}(?!\w)", RegexOptions.IgnoreCase));

    private static bool IsGlossaryHallucination(GlossaryLine item, List<GlossaryLine> allGlossaryLines, string preparedRaw, string translated, TextFileToSplit textFile)
    {
        translated = translated.Replace('\u2011', '-');
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
        if (!WholeWordRegex(item.Result).IsMatch(translated))
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

    /// <summary>
    /// Active bad-words list - deliberately just "knight" and its inflections today: this is a
    /// wuxia setting, and "knight" is a jarring Western chivalric term that has no place in it
    /// (unlike the other, now-commented-out entries below, which were transient false positives
    /// from unrelated languages, not an intentional ban). Every other entry stays commented out as
    /// a record of past false positives rather than removed - see git history for how each one got
    /// there. Whole-word (not substring) and case-insensitive, see <see cref="FindBadWordMatches"/>.
    /// </summary>
    private static readonly string[] BadWords =
    [
        //"hiu", "tut", "thut", "oi", "avo", "porqe", "obrigado",
            "knight", "knights", "knight-at-arms", "knights-errant",
            //"nom", "esto", "tem", "mais", "com", "ver", "nos", "sobre", "vermos",
            //"dar", "nam", "J'ai", "je", "veux", "pas", "ele", "una", "keqi", "shiwu",
            //"ich", "ein", "der", "ganzes", "Leben", "dort", //"de", NAmes can have de
            //"thay", "tien", "div", "html", "tiantu", "ngoc", "truong", "Phong"
    ];

    private static readonly Regex BadWordsPattern = new($@"\b({string.Join("|", BadWords)})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool MatchesBadWords(string input) => BadWordsPattern.IsMatch(input);

    /// <summary>
    /// Every distinct bad-words-list term found in <paramref name="input"/> (empty if none) - used
    /// both to build a specific, actionable <see cref="RuleCheckResult.BadWordsReason"/> (rather
    /// than a bare "matches the list" with no indication of which word) and by
    /// <see cref="Workflow.QualityControlWorkflow.GetLlmVerdictAsync"/>'s inline retry loop to tell
    /// the model exactly what to avoid on its next attempt.
    /// </summary>
    internal static IReadOnlyList<string> FindBadWordMatches(string input) =>
        BadWordsPattern.Matches(input)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();


    public static async Task ResetAllFlags(string workingDirectory, TextFileToSplit[] textFiles)
    {
        await MutateSplitsAsync(workingDirectory, textFiles, split =>
        {
            var hadFlags = split.FlaggedForRetranslation
                || !string.IsNullOrEmpty(split.FlaggedMistranslation)
                || !string.IsNullOrEmpty(split.FlaggedHallucination);

            // Reset all the retrans flags
            split.ResetFlags(false);
            return hadFlags;
        }, logWrites: false);
    }

    public static async Task SetSplitAsInvalid(string workingDirectory,
        TextFileToSplit[] textFiles,
        List<string> badStrings)
    {
        await MutateSplitsAsync(workingDirectory, textFiles, split =>
            badStrings.Any(s => split.Text.Contains(s)) && FlagAsBadCharacter(split));
    }

    public static async Task SetSplitAsInvalidByRegex(string workingDirectory,
        TextFileToSplit[] textFiles,
        List<string> badPatterns)
    {
        var regexes = badPatterns.Select(p => new Regex(p)).ToList();

        await MutateSplitsAsync(workingDirectory, textFiles, split =>
            regexes.Any(r => r.IsMatch(split.Text)) && FlagAsBadCharacter(split));
    }

    public static async Task CleanUpSomeRegexes(string workingDirectory,
        TextFileToSplit[] textFiles,
        List<(string pattern, string replacement)> regex)
    {
        var replacements = regex.Select(r => (Regex: new Regex(r.pattern), r.replacement)).ToList();

        await MutateSplitsAsync(workingDirectory, textFiles, split =>
        {
            if (!replacements.Any(r => r.Regex.IsMatch(split.Translated)))
                return false;

            var original = split.Translated;
            foreach (var (pattern, replacement) in replacements)
                split.Translated = pattern.Replace(split.Translated, replacement);

            return split.Translated != original;
        });
    }

    private static bool FlagAsBadCharacter(TranslationSplit split)
    {
        split.FlaggedForRetranslation = true;
        split.FlaggedMistranslation = "Bad Character";
        return true;
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to every split of every converted file (files in parallel)
    /// and writes back only the files where it reported a change.
    /// </summary>
    private static async Task MutateSplitsAsync(string workingDirectory, TextFileToSplit[] textFiles,
        Func<TranslationSplit, bool> mutate, bool logWrites = true)
    {
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory,
            textFiles,
            async (outputFile, textFileToTranslate, fileLines) =>
        {
            var recordsModded = 0;

            foreach (var line in fileLines)
                foreach (var split in line.Splits)
                    if (mutate(split))
                        recordsModded++;

            if (recordsModded == 0)
                return;

            await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));

            if (logWrites)
                Console.WriteLine($"Writing {recordsModded} records to {outputFile}");
        });
    }
}
