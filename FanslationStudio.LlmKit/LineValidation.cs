using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit;

public static partial class LineValidation
{
    public const string ChineseCharPattern = @".*\p{IsCJKUnifiedIdeographs}.*";
    public const string ChinesePlaceholderPattern = @"\{[a-zA-Z]*\s*\p{IsCJKUnifiedIdeographs}+\}";
    public const string PlaceholderMatchPattern = @"(\{[^{}]+\})";

    // Compiled / source-generated regexes — one instance shared across all calls
    public static Regex ChineseCharPatternCompiled => ChineseCharRegex();

    /// <summary>True if <paramref name="input"/> contains any CJK unified ideograph. Prefer this
    /// over <c>Regex.IsMatch(input, ChineseCharPattern)</c> - same answer, without the
    /// <c>.*</c> wrappers' backtracking or the static Regex cache lookup.</summary>
    public static bool ContainsCjk(string? input) => !string.IsNullOrEmpty(input) && CjkCharRegex().IsMatch(input);

    /// <summary>True if <paramref name="input"/> contains a <c>{...}</c> placeholder whose name
    /// is CJK text (see <see cref="ChinesePlaceholderPattern"/>).</summary>
    public static bool ContainsChinesePlaceholder(string? input) => !string.IsNullOrEmpty(input) && ChinesePlaceholderRegex().IsMatch(input);

    // LLM meta-commentary/instruction-leak signatures - the model narrating its own
    // translation process instead of just returning the translation   
    private static readonly string[] InvalidPhrases =
    [
        "etc.",
        "provide the text",
        "Certainly! Please provide the Chinese",
        "Certainly! Please provide the specific Chinese",
        "It seems like your input might be incomplete or missing some context",
        "Please provide the Chinese string you would like to be translated into English",
        "please provide the Chinese string",
        "please provide the specific Chinese strings",
        "removed from the translation",
        "Chinese text",
        "Chinese sentence",
        "translates to",
        "It seems that the text",
        "'''",
        "<p", "</p", "<em", "</em", "<|", "<strong", "</strong",
        "\\U",
        "cultural nuance",
        "gender‑neutral language",
        "gender-neutral language",
        "Output only the",
        "the translation remains",
        "fully corrected English translation",
        "Translate all Chinese characters",
        "untranslated Chinese characters",
        "English equivalent",
        "more natural English",
        "could be translated"
    ];

    private static readonly (string raw, string trans)[] CheckForRemoval = [];

    public static string PrepareRaw(string raw, StringTokenReplacer? tokenReplacer)
    {
        // Clean up the Raw string before using

        //StripColorTags(raw)
        raw = raw
            //.Replace("。", ".") //Hold off on this one for now
            .Replace("…", "...")
            .Replace("：", ":")
            .Replace("：", ":")
            //.Replace("「", "'")
            //.Replace("」", "'")
            //.Replace("《", "'")
            //.Replace("》", "'")
            .Replace("（", "(")
            .Replace("）", ")")
            .Replace("？", "?")
            .Replace("、", ",")
            .Replace("，", ",")
            .Replace("！", "!");

        //if (raw.Contains("<"))
        //    raw = HtmlTagValidator.TrimHtmlTagsInContent(raw);

        //For testing
        if (tokenReplacer != null)
            raw = tokenReplacer.Replace(raw);

        return raw;
    }

    /// <summary>
    /// Fix up anything we know the LLM has messed up but can autocorrect before validation.
    /// <paramref name="hooks"/> carries the caller's optional, game-specific repair hooks (see
    /// <see cref="Configuration.GameHooks"/>) - pass <c>config.Hooks</c> from whatever
    /// <see cref="Configuration.LlmConfig"/> is in scope at the call site.
    /// </summary>
    public static string PrepareResult(string raw, string llmResult, GameHooks? hooks, TextFileToSplit? textFile = null, int? column = null)
    {
        // Easy way to fix ...
        if (raw.EndsWith("...") && !llmResult.EndsWith("...") && llmResult.EndsWith("."))
            llmResult = $"{llmResult}..";

        var result = llmResult
            .Replace("’", "'")
            .Replace("‘", "'");

        if (hooks?.CustomPostRepair != null)
            result = hooks.CustomPostRepair(raw, result);

        if (hooks?.CustomColumnRepair != null)
            result = hooks.CustomColumnRepair(textFile, column, raw, result);

        return result;
    }

    public static string CleanupLineBeforeSaving(string input, string raw, TextFileToSplit textFile, StringTokenReplacer tokenReplacer)
    {
        //Finalise line before saving out
        var result = input.Trim();

        if (!string.IsNullOrEmpty(result))
        {
            if (result.Contains('\"') && !raw.Contains('\"'))
                result = result.Replace("\"", "");

            //if (!StringTokenReplacer.EmojiItems.Any(phrase => result.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0))
            //{
            //if (result.Contains('[') && !raw.Contains('['))
            //    result = result.Replace("[", "");

            //if (result.Contains(']') && !raw.Contains(']'))
            //    result = result.Replace("]", "");
            //}

            if (result.Contains('`') && !raw.Contains('`'))
                result = result.Replace("`", "'");

            // Take out wide quotes
            if (result.Contains('“') && !raw.Contains('“'))
                result = result.Replace("“", "");

            if (result.Contains('”') && !raw.Contains('”'))
                result = result.Replace("”", "");

            // Take out wierd ** being added
            if (result.Contains("**") && !raw.Contains("**"))
                result = result.Replace("**", "");

            result = result
                .Replace("…", "...")
                .Replace("？", "?")
                .Replace(".:", ":")
                .Replace(". -", " -")
                .Replace("！", "!");

            //Take out wide quotes and line split items
            result = result
                .Replace("。", ".")
                .Replace("’", "'")
                .Replace("‘", "'")
                .Replace("—", "-")
                .Replace("-", "\u2011") //Change Hyphens to non breaking hyphens
                .Replace("{‑1}", "{-1}"); // Change special {-1} non breaking hyphen back to normal hyphen

            //Strip .'s
            //if (result.EndsWith('.') && !raw.EndsWith(".") && !result.EndsWith(".."))
            //    result = result[..^1];

            if (textFile.RemoveNumbers)
                result = RemoveNumbers(result);

            if (textFile.NameCleanupRoutines || textFile.NameCleanupRoutines2)
            {
                if (textFile.NameCleanupRoutines)
                    result = result.Replace(" ", "");
                else if (textFile.NameCleanupRoutines2)
                {
                    if (!CjkCharRegex().IsMatch(input))
                    {
                        var splits = result.Split(" ", StringSplitOptions.RemoveEmptyEntries);
                        switch (splits.Length)
                        {
                            case 1:
                                result = "";
                                break;
                            case 2:
                                break;
                            case 3:
                                result = $"{splits[0]} {splits[1]}{splits[2]}";
                                break;
                            case 4:
                                result = $"{splits[0]}{splits[1]} {splits[2]}{splits[3]}";
                                break;
                            case 5:
                                result = $"{splits[0]}{splits[1]} {splits[2]}{splits[3]}{splits[4]}";
                                break;
                            default:
                                break;
                        }
                    }
                }

                result = result.Replace(".", "");
                result = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(result);
            }

            if (textFile.RemoveExtraFullStop)
                result = RemoveFullStop(raw, result);

            if (textFile.RemoveExtraThe)
                result = RemoveExtraThe(raw, result);

            result = RemoveDiacritics(result);
            result = ReplaceIncorrectLowercaseWords(result);
            result = EncaseColorsForWholeLines(raw, result);
            result = EncaseSquareBracketsForWholeLines(raw, result);
            result = FixUnbalancedParentheses(raw, result);
            result = FixUnbalancedQuotes(raw, result);

            if (string.IsNullOrEmpty(result))
            {
                Console.WriteLine($"Something Bad happened somewhere: {raw}\n{result}");
                return result;
            }

            if (result.StartsWith('\'') && result.EndsWith('\''))
                if (result.Length > 3)
                    result = result[1..^1];

            if (Char.IsLower(result[0]) && raw != result)
                result = Char.ToUpper(result[0]) + result[1..];
        }

        result = tokenReplacer.Restore(result);

        //TODO: Do a way where we can do regexes in text and replace with common templates like achieves in HTLS
        //result = result.Replace("友好到达", " friendship reached ");

        result = result
            .Replace("⑩", "10. ")
            .Replace("⓪", "0. ")
            .Replace("①", "1. ")
            .Replace("②", "2. ")
            .Replace("③", "3. ")
            .Replace("④", "4. ")
            .Replace("⑤", "5. ")
            .Replace("⑥", "6. ")
            .Replace("⑦", "7. ")
            .Replace("⑧", "8. ")
            .Replace("⑨", "9. ");

        return result;
    }

    public static ValidationResult CheckTransalationSuccessful(ModelExecutionConfig config, string raw, string result, TextFileToSplit textFile, GameHooks? hooks = null, int? column = null)
    {
        var response = true;
        var correctionPrompts = new StringBuilder();

        // A stray '\r' (alone, or as the CRLF a model occasionally emits instead of a bare '\n')
        // carries no meaning of its own in this pipeline - unlike a genuine embedded '\n' (see the
        // duplication check below), its mere presence isn't a sign the model duplicated/
        // self-corrected text, so flagging it invalid just spends a retry (or, on the QC path,
        // rejects an otherwise-good correction) fixing something that's easier to normalize away
        // outright. Folding it into '\n' here (rather than dropping it) means a genuine
        // duplication - e.g. "\r" used the same way the '\n' check below guards against - still
        // gets caught by that same check afterwards. Left alone if raw itself already contains a
        // '\r' (e.g. genuinely CRLF source text), since then it isn't something the model added.
        if (result.Contains('\r') && !raw.Contains('\r'))
            result = result.Replace("\r\n", "\n").Replace('\r', '\n');

        var silentFailures = new List<string>();

        if (string.IsNullOrEmpty(raw))
        {
            response = false;
            silentFailures.Add("The source text is empty.");
        }

        // A phrase the source itself contains (e.g. "\U" in a "C:\Users\..." path) is not model chatter.
        var invalidPhrase = InvalidPhrases.FirstOrDefault(phrase =>
            result.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0
            && raw.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) < 0);
        if (invalidPhrase != null)
        {
            response = false;
            silentFailures.Add($"Contains the invalid phrase '{invalidPhrase}'.");
        }

        // 99% chance its gone crazy with hallucinations
        if (result.Length > 50 && raw.Length <= 4)
        {
            response = false;
            silentFailures.Add("Result is far too long for a source of 4 characters or fewer.");
        }

        if (result.Length > raw.Length * 15)
        {
            response = false;
            silentFailures.Add("Result is more than 15 times longer than the source.");
        }

        // Small source with 'or' is usually an alternative
        if ((result.Contains(" or") || result.Contains("(or"))
            && raw.Length <= 3
            && !result.Contains("ore", StringComparison.OrdinalIgnoreCase)) //Handle edge case
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectAlternativesPrompt", "or");
        }

        // Small source with 'and' is ususually an alternative
        //if (result.Contains(" and") && raw.Length < 3 && !result.Contains("Spear and Staff", StringComparison.OrdinalIgnoreCase))
        //{
        //    response = false;
        //    correctionPrompts.AddPromptWithValues(config, "CorrectAlternativesPrompt", "and");
        //}

        // Small source with ';' is ususually an alternative
        if (result.Contains(';') && !raw.Contains(';') && raw.Length < 4)
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectAlternativesPrompt", ";");
        }

        if (result.Contains(',') && !raw.Contains(',') && !raw.Contains("，") && !raw.Contains("、") && raw.Length < 4)
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectAlternativesPrompt", ",");
        }

        // Added literal
        if (result.Contains("(lit."))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectExplainationPrompt");
        }

        // Removed :
        if (raw.Contains(':') && !result.Contains(':') && !raw.Contains(":'"))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectColonSegementPrompt");
        }

        // A raw cell that starts with a comma ("，" or plain ",") is a fragment that continues
        // directly from the previous cell/sentence (e.g. a compound field split by
        // CompoundFieldSplitter). The model routinely relocates the comma into a natural-sounding
        // construction instead of keeping it as the leading character - e.g. raw
        // "，比方说这太祖长拳，" mistranslated as "For example, this Taijiquan" instead of
        // ", for example, this Taijiquan" - silently losing the fragment-boundary marker.
        if ((raw.StartsWith('，') || raw.StartsWith(',')) && !(result.StartsWith(", ") || result.StartsWith('，')))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectLeadingCommaPrompt");
        }

        //Place holders - incase the model ditched them
        // Compared by per-token occurrence count so a duplicated/dropped repeat ("{0} ... {0}"
        // reduced to one "{0}") and an invented extra ("{1}" when raw only has "{0}") are both caught.
        var rawPlaceholderCounts = PlaceholderPatternRegex().Matches(raw)
            .GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.Count());
        var resultPlaceholderCounts = PlaceholderPatternRegex().Matches(result)
            .GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.Count());

        foreach (var (token, rawCount) in rawPlaceholderCounts)
        {
            if (resultPlaceholderCounts.GetValueOrDefault(token) < rawCount)
            {
                response = false;
                correctionPrompts.AddPromptWithValues(config, "CorrectRemovalPrompt", token);
            }
        }

        foreach (var (token, resultCount) in resultPlaceholderCounts)
        {
            if (resultCount > rawPlaceholderCounts.GetValueOrDefault(token))
            {
                response = false;
                correctionPrompts.AddPromptWithValues(config, "CorrectAdditionalPrompt", token);
            }
        }

        if (raw.Contains('\'') && !result.Contains('\''))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectRemovedQuotesPrompt");
        }

        // Removed characters
        foreach (var check in CheckForRemoval)
        {
            if (raw.Contains(check.raw) && !result.Contains(check.trans))
            {
                response = false;
                correctionPrompts.AddPromptWithValues(config, "CorrectRemovalPrompt", check.raw);
            }
        }

        // A source \n immediately preceded by a comma (or nothing terminal) is a mid-sentence
        // UI line-wrap - the devs split one continuing sentence across two lines purely for
        // chat-bubble/box width. Forcing that literal break into the English translation produces
        // a comma-splice artifact ("...camp,\nIt seems...") instead of the single fluent sentence
        // a native speaker would write, so only hard-enforce \n preservation when it sits between
        // two already-complete sentences/clauses (preceded by 。！？!?.) - that's the case closer
        // to a deliberate structural/stat-list-style separator worth protecting.
        var hasStructuralNewlineBreak = Regex.IsMatch(raw, @"[。！？!?.]\s*\\n");
        if (hasStructuralNewlineBreak && raw.Contains("\\n") && !result.Contains("\\n"))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectRemovalPrompt", "\\n");
        }

        //if (raw.Contains('-') && !result.Contains('-') && !result.Contains("\u2011"))
        //{
        //    response = false;
        //    correctionPrompts.AddPromptWithValues(config, "CorrectRemovalPrompt", "-");
        //}

        // This can cause bad hallucinations if not being explicit on retries
        if (raw.Contains("<br>") && !result.Contains("<br>"))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectRemovalPrompt", "<br>");
            //correctionPrompts.AddPromptWithValues(config, "CorrectTagPrompt");
        }
        // Color tags are evil
        //else if (raw.Contains("<color") && !result.Contains("<color"))
        //{
        //    response = false;
        //    correctionPrompts.AddPromptWithValues(config, "CorrectRemovalPrompt", "<color>");
        //    correctionPrompts.AddPromptWithValues(config, "CorrectTagPrompt");
        //}                

        // Some raws dont have both because they are dynamic strings
        // Color invalidation - if it has a start tag but no end tag
        if (result.Contains("<color") && raw.Contains("</color>") && !result.Contains("</color>"))
        {
            response = false;
            silentFailures.Add("A <color> tag is opened but never closed.");
        }
        // Color invalidation - if it has a end tag but no start tag
        if (result.Contains("</color") && raw.Contains("<color") && !result.Contains("<color"))
        {
            response = false;
            silentFailures.Add("A </color> tag is closed but never opened.");
        }

        // Random additions
        if (result.Contains("<br>") && !raw.Contains("<br>"))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectAdditionalPrompt", "<br>");
        }

        if (result.Contains('\n') && !raw.Contains('\n'))
        {
            // A genuine embedded newline (as opposed to the literal two-char "\n" escape used
            // throughout these CSVs) is a strong signal the model duplicated/self-corrected
            // mid-response (e.g. "Wan, extremely sorry!\nExtremely sorry!\n...") rather than a
            // deliberate paragraph break - replacing it with a space alone would just cosmetically
            // join the duplicated text instead of fixing it, and it also breaks CSV column counts
            // once written out. Flag as invalid so it gets retried instead of silently patched.
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectAdditionalPrompt", "\\n");
        }

        if (CjkCharRegex().IsMatch(result) && !ChinesePlaceholderRegex().IsMatch(result))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectChinesePrompt");

            // Flag for sentence-by-sentence correction strategy
            var validationResult = new ValidationResult
            {
                Valid = response,
                Result = result,
                CorrectionPrompt = correctionPrompts.ToString(),
                RequiresSentenceBySentenceCorrection = true,
                SilentFailures = silentFailures,
            };
            return validationResult;
        }

        // Dialog specific
        // Added Brackets (Literation) where no brackets or widebrackets in raw
        if (result.Contains('(') && !raw.Contains('(') && !raw.Contains('（'))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectExplainationPrompt");
        }

        // Wide brackets dropped from the translation. Each family lists what counts as "kept":
        // the wide bracket itself, the ASCII form PrepareRaw normalises it to, or a natural swap.
        var droppedBracket = FindDroppedWideBracket(raw, result);
        if (droppedBracket != null)
        {
            response = false;
            // Falls back to the generic removal prompt for custom prompt sets that predate this key.
            correctionPrompts.AddPromptWithValues(config,
                config.Prompts.ContainsKey("CorrectWideBracketPrompt") ? "CorrectWideBracketPrompt" : "CorrectRemovalPrompt",
                droppedBracket);
        }

        ////Alternatives
        if (result.Contains('/') && !raw.Contains('/'))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectAlternativesPrompt", "/");
        }

        // Exclude the literal "\n" escape - this pipeline's own convention for an embedded line
        // break (which BaseQualityReviewPrompt.txt explicitly requires the QC model to use when
        // joining a multi-sentence correction) - so a correctly-formed correction doesn't get
        // mistaken for a stray backslash/alternative.
        if (result.Replace("\\n", "").Contains('\\') && !raw.Contains('\\'))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectAlternativesPrompt", "\\");
        }

        if (raw.Contains('<') && raw != "<商贩>" && !textFile.IgnoreHtmlTagsInText)
        {
            var validateTags = HtmlTagHelpers.ValidateTags(raw, result, textFile.AllowMissingColorTags);
            if (!validateTags.IsValid)
            {
                response = false;

                foreach (var tag in validateTags.MissingTags)
                    correctionPrompts.AddPromptWithValues(config, "CorrectRemovalPrompt", $"<{tag}>");

                foreach (var tag in validateTags.ExtraTags)
                    correctionPrompts.AddPromptWithValues(config, "CorrectAdditionalPrompt", $"<{tag}>");
            }
        }

        if (textFile.NameCleanupRoutines)
        {
            if ((raw.Length == 1 && result.Length > 6)
                || (raw.Length == 2 && result.Length > 12)
                || (raw.Length == 3 && result.Length > 17))
            {
                response = false;
                silentFailures.Add("Result is too long for a short name.");
            }
        }

        if (hooks?.CustomColumnValidator != null)
        {
            var customFailureReason = hooks.CustomColumnValidator(textFile, column, raw, result);
            if (customFailureReason != null)
            {
                response = false;

                // The reason string's directionality determines which correction prompt actually
                // describes the defect: a hook reporting a token present in raw but missing from
                // result (e.g. a dropped "#PlayerName#"-style placeholder) is a REMOVAL, not an
                // addition - sending "CorrectAdditionalPrompt" ("has been added to the result but
                // was not in the original text") for a missing token tells the model the exact
                // opposite of what's wrong and confuses the retry loop. Only fall back to
                // "CorrectAdditionalPrompt" when the reason genuinely describes something extra
                // that appeared in result but wasn't in raw; default to "CorrectRemovalPrompt" for
                // anything else, since most custom-column hooks report a missing/dropped token.
                if (result.Contains(customFailureReason) && !raw.Contains(customFailureReason))
                    correctionPrompts.AddPromptWithValues(config, "CorrectAdditionalPrompt", customFailureReason);
                else
                    correctionPrompts.AddPromptWithValues(config, "CorrectRemovalPrompt", customFailureReason);
            }
        }

        return new ValidationResult
        {
            Valid = response,
            Result = result,
            CorrectionPrompt = correctionPrompts.ToString(),
            SilentFailures = silentFailures,
            SoftCorrectionPrompt = response && InventsGender(raw, result) && config.Prompts.TryGetValue("CorrectInventedGenderPrompt", out var genderPrompt)
                ? genderPrompt
                : string.Empty,
        };
    }

    /// <summary>
    /// True when <paramref name="translated"/> probably names someone: a capitalised word of three or more letters
    /// that does not start a sentence. Titles such as "Sect Leader" count too, which errs towards skipping.
    /// </summary>
    public static bool NamesSomeone(string translated) =>
        CapitalisedWordRegex().Matches(translated).Any(match => !StartsSentence(translated, match.Index));

    private static bool StartsSentence(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
            i--;

        return i < 0 || text[i] is '.' or '!' or '?' or '(' or ':' or '"' or '\u201C';
    }

    /// <summary>
    /// True when <paramref name="translated"/> uses a pronoun for the opposite gender to the known
    /// <paramref name="gender"/> and <paramref name="raw"/> states no gender of its own (<see cref="LineContext.Male"/> / <see cref="LineContext.Female"/>) and none
    /// for the right one. A line that names a second person can legitimately use both, so it is only a candidate
    /// to re-check, not proof of an error.
    /// </summary>
    public static bool ContradictsGender(string raw, string translated, string gender)
    {
        // A source that states a gender itself (他/她, a kinship term or title) can name a second person of either
        // gender, so a pronoun that differs from the speaker's is expected there.
        if (GenderedSourceRegex().IsMatch(raw))
            return false;

        var male = MalePronounRegex().IsMatch(WithoutNameTails(translated));
        var female = FemalePronounRegex().IsMatch(WithoutNameTails(translated));
        return gender == LineContext.Male ? female && !male : gender == LineContext.Female && male && !female;
    }

    /// <summary>
    /// Like <see cref="ContradictsGender"/>, but for a source whose only gender signal is a kinship term or title
    /// (妹妹, 先生...) with no explicit 他/她: e.g. a female speaker's "（妹妹说错了）" translated "His younger sister
    /// was wrong". The pronoun may belong to the relative rather than the speaker, so this is a lower-confidence
    /// candidate for a re-check, never proof.
    /// </summary>
    public static bool ContradictsGenderDespiteKinshipTerm(string raw, string translated, string gender)
    {
        if (!GenderedSourceRegex().IsMatch(raw) || ExplicitPronounSourceRegex().IsMatch(raw))
            return false;

        var male = MalePronounRegex().IsMatch(WithoutNameTails(translated));
        var female = FemalePronounRegex().IsMatch(WithoutNameTails(translated));
        return gender == LineContext.Male ? female && !male : gender == LineContext.Female && male && !female;
    }

    /// <summary>True when <paramref name="translated"/> refers to someone only as "they/them/their", with no he/she at all.</summary>
    public static bool UsesOnlyNeutralPronouns(string translated) =>
        NeutralPronounRegex().IsMatch(translated) && !GenderedPronounRegex().IsMatch(WithoutNameTails(translated));

    /// <summary>
    /// True when <paramref name="raw"/> is subject-less narration (只见, 只听, 行至, 忽然听闻...) that names no
    /// speaker of its own, yet <paramref name="result"/> narrates it as "I/me/my". The game narrates to the
    /// player in second person, so this should read "you" or have no subject. Spoken lines and parenthesised
    /// thoughts are not matched, nor is a line that mentions 你/您 (another character addressing the player, so "I" is right).
    /// </summary>
    /// <summary>
    /// True when <paramref name="raw"/> itself states a gender: 他/她/它 or a gendered kinship term or title (师兄, 姑娘...).
    /// A pronoun for someone else in such a line cannot be judged from a named character's gender alone.
    /// </summary>
    public static bool SourceStatesGender(string raw) => GenderedSourceRegex().IsMatch(raw);

    public static bool NarratesAsFirstPerson(string raw, string result) =>
        raw.Length <= 80 && NarrationOpenerRegex().IsMatch(raw) && !SelfReferenceRegex().IsMatch(raw) && !raw.Contains('你') && !raw.Contains('您') && FirstPersonPronounRegex().IsMatch(result);

    /// <summary>
    /// True when <paramref name="result"/> gives someone a he/she/his/her/him that the short, ungendered
    /// <paramref name="raw"/> never established: a parenthesised stage direction or an unnamed-role line
    /// (此人, 对方, 乞丐...) with no 他/她 or gendered kinship/title character. Deliberately narrow - a
    /// named character in running narration is left alone rather than forced into "they".
    /// </summary>
    public static bool InventsGender(string raw, string result, bool skipWhenResultNamesSomeone = false, IReadOnlyCollection<string>? unknownGenderTokens = null)
    {
        // In running prose a pronoun after a named character is usually right (the game, or an earlier sentence,
        // established who they are), which a single line cannot tell apart from an invented one.
        if (skipWhenResultNamesSomeone && NamesSomeone(result))
            return false;

        if (!GenderedPronounRegex().IsMatch(WithoutNameTails(result)) || GenderedSourceRegex().IsMatch(raw))
            return false;

        // A token for someone whose gender is unknown (the player, a runtime-chosen person) - the pronoun is invented
        // however the line is shaped. Tokens lengthen a line, so allow a little more room. A translation that also names
        // someone is skipped: the pronoun may belong to that person, not to the token.
        if (unknownGenderTokens != null && raw.Length <= 100 && unknownGenderTokens.Any(token => raw.Contains(token, StringComparison.Ordinal)))
            return !NamesSomeone(result);

        if (raw.Length > 60)
            return false;

        var trimmed = raw.TrimStart();
        return trimmed.StartsWith('(') || trimmed.StartsWith('（') || UnnamedRoleRegex().IsMatch(raw);
    }

    private static readonly (string WideChars, string AcceptableInResult)[] WideBracketFamilies =
    [
        ("（）", "（）()"),
        //("【】［］〔〕", "【】［］〔〕[]"),
        // 《》 (book-title marks) and 「」『』 (corner brackets) deliberately not checked: idiomatic English
        // drops them ("the Taoist classic Zhuangzi", Command: Verbal Assault), so requiring them forces
        // awkward translations and endless retries.
    ];

    /// <summary>
    /// Returns the first wide (CJK) bracket in <paramref name="raw"/> whose whole family has vanished
    /// from <paramref name="result"/>, or null if none were dropped.
    /// </summary>
    public static string? FindDroppedWideBracket(string raw, string result)
    {
        foreach (var (wideChars, acceptable) in WideBracketFamilies)
        {
            var rawBracket = raw.FirstOrDefault(c => wideChars.Contains(c));
            if (rawBracket != default && !result.Any(c => acceptable.Contains(c)))
                return rawBracket.ToString();
        }

        return null;
    }

    public static List<string> FindMarkup(string input)
    {
        var markupTags = new List<string>();

        if (input == null)
            return markupTags;

        // Regular expression to match markup tags in the format <tag>
        var matches = HtmlTagRegex().Matches(input);

        // Add each match to the list of markup tags
        foreach (Match match in matches)
            markupTags.Add(match.Value);

        return markupTags;
    }

    public static string EncaseColorsForWholeLines(string raw, string translated)
    {
        if (raw.StartsWith("<color") && raw.EndsWith("</color>")
            && raw.LastIndexOf("<color") == 0 && !translated.StartsWith("<color"))
        {
            var matches = EncaseColorTagRegex().Matches(raw);
            string start = matches[0].Groups[1].Value;
            string end = matches[0].Groups[2].Value;
            translated = $"{start}{translated}{end}";
        }

        return translated;
    }

    public static string EncaseSquareBracketsForWholeLines(string raw, string translated)
    {
        if (raw.StartsWith('【')
            && raw.EndsWith('】')
            && !translated.Contains('【')
            && !translated.Contains('】'))
        {
            translated = $"【{translated}】";
        }

        return translated;
    }

    /// <summary>
    /// A raw cell that only contains one side of a parenthetical (e.g. "（卓远望...离去，" - an
    /// opening "（" with no closing "）") means the parenthetical aside continues in a different
    /// cell/row of the same conversation rather than being unbalanced/malformed in the source data.
    /// Two symmetric failure modes have been observed: the model "helpfully" closes the bracket it
    /// opened (or opens one to match a closing bracket it's translating) even though nothing in the
    /// raw asked it to, producing a self-contained, balanced-looking "(...)"; or the model drops
    /// the dangling bracket character entirely, losing the marker altogether. Either way, the
    /// translated output should end up with exactly the same one-sided bracket the raw has - no
    /// spuriously added counterpart, and the original marker preserved if the model dropped it.
    /// </summary>
    public static string FixUnbalancedParentheses(string raw, string result)
    {
        if (string.IsNullOrEmpty(result))
            return result;

        var rawHasOpen = raw.Contains('(') || raw.Contains('（');
        var rawHasClose = raw.Contains(')') || raw.Contains('）');

        // Balanced (or absent) in raw - nothing to reconcile.
        if (rawHasOpen == rawHasClose)
            return result;

        if (rawHasOpen && !rawHasClose)
        {
            // Raw only opens a parenthetical (it closes in a later cell) - remove any closing
            // paren the model added on its own since there's nothing here for it to close.
            if (result.Contains(')'))
                result = result.Remove(result.LastIndexOf(')'), 1);

            // Make sure the dangling opening paren itself survived translation.
            if (!result.Contains('('))
                result = $"({result}";
        }
        else if (rawHasClose && !rawHasOpen)
        {
            // Raw only closes a parenthetical (it opened in an earlier cell) - remove any
            // opening paren the model added on its own.
            if (result.Contains('('))
                result = result.Remove(result.IndexOf('('), 1);

            // Make sure the dangling closing paren itself survived translation.
            if (!result.Contains(')'))
                result = $"{result})";
        }

        return result;
    }

    /// <summary>
    /// Same one-sided-continues-in-another-cell problem as <see cref="FixUnbalancedParentheses"/>,
    /// but for the game's quote/title markers - raw uses "“"/"”" for quoted speech and
    /// "《"/"》" for work/technique titles, and both consistently get translated to a plain
    /// single-quote pair (e.g. raw "《合盘掌》" -&gt; translated "'He Pan Palm'"), so a raw cell with
    /// only one side of either pair means the model shouldn't have produced a self-contained
    /// 'balanced' quote in the translation. As with parentheses, two symmetric failure modes have
    /// been observed: the model adds a spurious matching quote at the other end, or it drops the
    /// dangling quote marker entirely. Unlike parentheses, ASCII "'" is ambiguous with
    /// contraction/possessive apostrophes ("don't", "it's"), so a candidate quote mark only counts
    /// as a genuine boundary quote when it doesn't have letters on both sides.
    /// </summary>
    public static string FixUnbalancedQuotes(string raw, string result)
    {
        if (string.IsNullOrEmpty(result))
            return result;

        var rawHasOpen = raw.Contains('“') || raw.Contains('《');
        var rawHasClose = raw.Contains('”') || raw.Contains('》');

        // Balanced (or absent) in raw - nothing to reconcile.
        if (rawHasOpen == rawHasClose)
            return result;

        bool IsBoundaryQuote(int i) =>
            result[i] == '\''
            && !(i > 0 && char.IsLetter(result[i - 1]) && i < result.Length - 1 && char.IsLetter(result[i + 1]));

        if (rawHasOpen && !rawHasClose)
        {
            // Dangling open marker (closes in a later cell) - remove a spuriously added closing
            // quote at the end, since nothing here should close.
            if (result.Length > 0 && IsBoundaryQuote(result.Length - 1))
                result = result[..^1];

            // Make sure the dangling opening quote itself survived translation.
            if (result.Length == 0 || !IsBoundaryQuote(0))
                result = $"'{result}";
        }
        else if (rawHasClose && !rawHasOpen)
        {
            // Dangling close marker (opened in an earlier cell) - remove a spuriously added
            // opening quote at the start.
            if (result.Length > 0 && IsBoundaryQuote(0))
                result = result[1..];

            // Make sure the dangling closing quote itself survived translation.
            if (result.Length == 0 || !IsBoundaryQuote(result.Length - 1))
                result = $"{result}'";
        }

        return result;
    }

    public static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder();

        foreach (var c in normalizedString)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string ReplaceIncorrectLowercaseWords(string input)
    {
        input = JianghuRegex().Replace(input, "Jianghu");
        input = WulinRegex().Replace(input, "Wulin");
        return input;
    }

    public static string RemoveNumbers(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Remove all digits from the string
        return DigitRegex().Replace(input, "");
    }

    public static string RemoveExtraThe(string raw, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;
        if (raw.Contains(' '))
            return input;
        if (input.StartsWith("The ") && !input.Contains('.'))
        {
            var words = input.
                Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length <= 5)
                return input[4..];
        }
        return input;
    }

    public static string RemoveFullStop(string raw, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        if (raw.Contains(' '))
            return input;

        var fullStop = '.';

        // Check if there's only one sentence (one full stop at the end)
        if (input.IndexOf(fullStop) == input.LastIndexOf(fullStop)
            && !input.Contains('!')
            && !input.Contains('?')
            && input.TrimEnd().EndsWith(fullStop))
        {
            // Count words
            var words = input.TrimEnd(fullStop).
                Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length <= 7)
            {
                return input.Replace(fullStop.ToString(), string.Empty); // Remove full stop leaving spaces
            }
        }

        return input;
    }

    [GeneratedRegex(PlaceholderMatchPattern)]
    private static partial Regex PlaceholderPatternRegex();

    [GeneratedRegex(ChineseCharPattern)]
    private static partial Regex ChineseCharRegex();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}")]
    private static partial Regex CjkCharRegex();

    [GeneratedRegex(ChinesePlaceholderPattern)]
    private static partial Regex ChinesePlaceholderRegex();

    /// <summary>
    /// Blanks a name that ends in "He" (晁和 becomes "Chao He") before the pronoun checks run, so the name is not mistaken
    /// for "he". Only a capitalised word directly before a capital "He" counts; a sentence-initial "He" after a full
    /// stop does not match.
    /// </summary>
    private static string WithoutNameTails(string text) => NameEndingInHeRegex().Replace(text, "$1_");

    [GeneratedRegex(@"\b([A-Z][a-z]+) He\b")]
    private static partial Regex NameEndingInHeRegex();

    [GeneratedRegex(@"\b(?:he|she|his|her|him|himself|herself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenderedPronounRegex();

    [GeneratedRegex(@"\b[A-Z][a-z]{2,}\b")]
    private static partial Regex CapitalisedWordRegex();

    [GeneratedRegex(@"\b(?:he|his|him|himself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MalePronounRegex();

    [GeneratedRegex(@"\b(?:she|her|hers|herself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FemalePronounRegex();

    [GeneratedRegex(@"\b(?:they|them|their|theirs|themselves)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NeutralPronounRegex();

    // 他/她/它 plus gendered kinship, titles and roles: any of these means the source does state a gender.
    [GeneratedRegex("[他她它牠哥姐弟妹父母爹娘兄嫂郎女男叔婶爷妻婆翁妇]|公子|姑娘|少爷|小姐|先生|夫人|丈夫|夫君|儿子|奶奶|好汉|大汉|汉子")]
    private static partial Regex GenderedSourceRegex();

    // An explicit pronoun in the source: the translation is following the source, so it is not an invented gender.
    [GeneratedRegex("[他她它牠]")]
    private static partial Regex ExplicitPronounSourceRegex();

    // Specific unnamed people only. 来人 ("guards!"), 有人 ("someone"), 旁人 ("others") and 何人 ("who") are summons or
    // indefinites, not a reference to one person, so a he/she near them is not an invented gender.
    [GeneratedRegex("此人|这人|那人|对方|乞丐|隐者|路人|店小二|小二")]
    private static partial Regex UnnamedRoleRegex();

    [GeneratedRegex(@"^[（(]?\s*(?:只见|只听|但见|眼见|行至|话音刚落|等了不多时|正[^，。,]{1,8}间)|忽然听闻|只听闻|只听得")]
    private static partial Regex NarrationOpenerRegex();

    // Words that make a first-person subject legitimate: the speaker names themself.
    [GeneratedRegex("我|咱|老子|老夫|在下|贫道|贫僧|本官|本座|为师|弟子|徒儿|小的|自己|某")]
    private static partial Regex SelfReferenceRegex();

    [GeneratedRegex(@"\b(?:I|[Mm]y|[Mm]e|myself)\b")]
    private static partial Regex FirstPersonPronounRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"(<[^>]+>).*(</[^>]+>)")]
    private static partial Regex EncaseColorTagRegex();

    [GeneratedRegex(@"[。！？!?.]\s*\n")]
    private static partial Regex StructuralNewlineBreakRegex();

    [GeneratedRegex(@"\d")]
    private static partial Regex DigitRegex();

    [GeneratedRegex(@"\bjianghu\b")]
    private static partial Regex JianghuRegex();

    [GeneratedRegex(@"\bwulin\b")]
    private static partial Regex WulinRegex();
}
