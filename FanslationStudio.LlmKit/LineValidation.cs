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
    /// Optional caller-supplied hook invoked at the very end of <see cref="PrepareResult"/> - i.e.
    /// immediately after every LLM call (each attempt in the main retry loop, and each round of
    /// <see cref="TranslationService.CorrectSentenceBySentenceAsync"/>) and before
    /// <see cref="CheckTransalationSuccessful"/> gets a chance to validate/flag the result. Lets a
    /// game-specific project (e.g. Tests/GameFileHandling.cs) deterministically fix up known LLM
    /// quirks - like a possessive/contraction suffix ending up glued inside a placeholder token,
    /// e.g. "#PlayerName's#" instead of "#PlayerName#'s" - instead of paying for a whole retry
    /// round-trip to fix something a plain string replace already knows how to repair. Receives
    /// (raw, llmResult) and must return the (possibly repaired) result. Left null (no-op) unless a
    /// caller opts in; this is intentionally left game-agnostic here in the shared library, same as
    /// <see cref="Utility.CompoundFieldSplitterOptions.PlaceholderPatterns"/>.
    /// </summary>
    public static Func<string, string, string>? CustomPostRepair { get; set; }

    /// <summary>
    /// Optional caller-supplied hook invoked at the very end of <see cref="CheckTransalationSuccessful"/>,
    /// only when every built-in check above has already passed. Lets a game-specific project (e.g.
    /// Tests/GameFileHandling.cs) add validation rules that only make sense for one specific column
    /// of one specific file - e.g. PlotData.csv's column 9 ("选项"/Choice) is a compound field
    /// ('|'-separated choice options, each further ';'-separated by
    /// <see cref="Utility.CompoundFieldSplitter"/> into literal template text) where an LLM
    /// bleeding a stray '|' into a translated fragment would silently desync
    /// GameDataController.SetChoiceDataTexts's indexing - but a plain "'|' appeared in the
    /// translation" rule would be wrong to apply file-wide/globally, since other columns (or other
    /// games entirely) may have '|' appear legitimately in natural translated text.
    /// Receives (textFile, column, raw, result) - column is the zero-based CSV column index the
    /// fragment came from when known (see <see cref="Support.TranslationSplit.Split"/>), or null
    /// when validation is running outside a column context (e.g. direct unit tests). Return null
    /// when the hook finds nothing wrong; return a non-null correction-prompt-style reason string
    /// to flag the result as invalid and feed that reason back into the retry/correction loop, same
    /// as the built-in checks above. Left null (no-op) unless a caller opts in; this is
    /// intentionally left game-agnostic here in the shared library, same as
    /// <see cref="CustomPostRepair"/> and <see cref="Utility.CompoundFieldSplitterOptions.PlaceholderPatterns"/>.
    /// </summary>
    public static Func<TextFileToSplit, int?, string, string, string?>? CustomColumnValidator { get; set; }

    /// <summary>
    /// Optional caller-supplied hook invoked at the end of <see cref="PrepareResult"/>, before
    /// <see cref="CustomColumnValidator"/>/<see cref="CheckTransalationSuccessful"/> ever run. Lets
    /// a game-specific project deterministically strip/repair characters that can NEVER legitimately
    /// appear in a translated fragment for one specific file+column - e.g. PlotData.csv's column 9
    /// choice-option fragments are always a single isolated Chinese run with no '|'/';' in the raw
    /// text at all (those are structural separators <see cref="Utility.CompoundFieldSplitter"/>
    /// deliberately never absorbs into a fragment), so any '|'/';' present in the *translated*
    /// fragment is unambiguously an LLM artifact and can be stripped outright rather than merely
    /// detected-and-retried via <see cref="CustomColumnValidator"/>. This prevents the structural
    /// corruption at the source instead of relying on a retry loop to eventually avoid it.
    /// Receives (textFile, column, raw, result) with the same semantics as
    /// <see cref="CustomColumnValidator"/>, and must return the (possibly repaired) result. Left
    /// null (no-op) unless a caller opts in; intentionally left game-agnostic here in the shared
    /// library, same as <see cref="CustomPostRepair"/>.
    /// </summary>
    public static Func<TextFileToSplit?, int?, string, string, string>? CustomColumnRepair { get; set; }

    public static string PrepareResult(string raw, string llmResult, TextFileToSplit? textFile = null, int? column = null)
    {
        // Fix up anything we know the LLM has messed up but can autocorrect before validation

        // Easy way to fix ...
        if (raw.EndsWith("...") && !llmResult.EndsWith("...") && llmResult.EndsWith("."))
            llmResult = $"{llmResult}..";

        var result = llmResult
            .Replace("’", "'")
            .Replace("‘", "'");

        if (CustomPostRepair != null)
            result = CustomPostRepair(raw, result);

        if (CustomColumnRepair != null)
            result = CustomColumnRepair(textFile, column, raw, result);

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
                    if (!ChineseCharRegex().IsMatch(input))
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

    public static ValidationResult CheckTransalationSuccessful(ModelExecutionConfig config, string raw, string result, TextFileToSplit textFile, int? column = null)
    {
        var response = true;
        var correctionPrompts = new StringBuilder();

        if (string.IsNullOrEmpty(raw))
            response = false;

        if (InvalidPhrases.Any(phrase => result.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0))
            response = false;

        // 99% chance its gone crazy with hallucinations
        if (result.Length > 50 && raw.Length <= 4)
            response = false;

        if (result.Length > raw.Length * 15)
            response = false;

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
        var matches = PlaceholderPatternRegex().Matches(raw);
        foreach (Match match in matches)
        {
            if (!result.Contains(match.Value))
            {
                response = false;
                correctionPrompts.AddPromptWithValues(config, "CorrectRemovalPrompt", match.Value);
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

        if (raw.Contains("\\n") && !result.Contains("\\n"))
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
        }
        // Color invalidation - if it has a end tag but no start tag
        if (result.Contains("</color") && raw.Contains("<color") && !result.Contains("<color"))
        {
            response = false;
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

        if (result.Contains('\r') && !raw.Contains('\r'))
        {
            // Same duplication/self-correction signal as the embedded '\n' check above (e.g.
            // "Wan, extremely sorry! \r Extremely sorry!") - a genuine carriage return character
            // has no business appearing in these single-line CSV values, so flag as invalid and
            // retry rather than silently squashing the duplicated text into one line.
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectAdditionalPrompt", "\\r");
        }

        if (ChineseCharRegex().IsMatch(result) && !ChinesePlaceholderRegex().IsMatch(result))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectChinesePrompt");

            // Flag for sentence-by-sentence correction strategy
            var validationResult = new ValidationResult
            {
                Valid = response,
                Result = result,
                CorrectionPrompt = correctionPrompts.ToString(),
                RequiresSentenceBySentenceCorrection = true
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

        ////Alternatives
        if (result.Contains('/') && !raw.Contains('/'))
        {
            response = false;
            correctionPrompts.AddPromptWithValues(config, "CorrectAlternativesPrompt", "/");
        }

        if (result.Contains('\\') && !raw.Contains('\\'))
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
                response = false;
        }

        if (CustomColumnValidator != null)
        {
            var customFailureReason = CustomColumnValidator(textFile, column, raw, result);
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
        };
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

    [GeneratedRegex(ChinesePlaceholderPattern)]
    private static partial Regex ChinesePlaceholderRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"(<[^>]+>).*(</[^>]+>)")]
    private static partial Regex EncaseColorTagRegex();

    [GeneratedRegex(@"\d")]
    private static partial Regex DigitRegex();

    [GeneratedRegex(@"\bjianghu\b")]
    private static partial Regex JianghuRegex();

    [GeneratedRegex(@"\bwulin\b")]
    private static partial Regex WulinRegex();
}
