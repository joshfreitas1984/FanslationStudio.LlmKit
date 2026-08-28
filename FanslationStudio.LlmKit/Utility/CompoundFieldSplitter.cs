using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Helpers for treating game data rows as real CSV records (respecting quoted fields) and for
/// decomposing/reconstructing individual CSV cells that pack multiple Chinese fragments together
/// with structural separators (';', '-', '&amp;', '|', etc.), e.g. BuildingData's action column.
/// Game-specific tokens (e.g. a "#PlayerName#" placeholder) that must never act as a fragment
/// boundary are opted into via <see cref="CompoundFieldSplitterOptions"/> passed to
/// <see cref="Decompose"/> - the shared library itself has no hardcoded knowledge of any one
/// game's placeholder syntax, since another game could just as easily use the same character
/// (e.g. '#') as a genuine structural separator instead.
/// </summary>
public static partial class CompoundFieldSplitter
{
    // Matches runs of Chinese characters that may have digits/decimal points glued directly to them
    // with no separator (e.g. "击败500人"), so numbers embedded mid-sentence stay in the same
    // translatable fragment instead of splitting the sentence around them. A leading sign ('+'/'-')
    // is absorbed only when directly followed by a digit (e.g. "-99表示自动"), so the sign travels
    // with its number instead of being stranded outside the fragment while the number itself still
    // ends up glued to the following sentence - that lets the LLM see "-99" as one unit and produces
    // a much safer translation than sending only "99表示自动" with a disconnected literal "-" beside
    // it (the LLM could otherwise reorder/drop the number, breaking the "-99" sentinel value). A
    // trailing '%' is absorbed the same way and the run then keeps extending through any further
    // CJK/digit text (e.g. "同盟区域50%后进入门派" stays one fragment, not split at '%').
    // Any full-width/CJK punctuation ('，' '。' '？' '！' '；' '、' '（）' '～' etc. - the
    // Unicode "CJK Symbols and Punctuation" and "Halfwidth and Fullwidth Forms" blocks) is also
    // absorbed into the run rather than treated as a fragment boundary: an LLM is free to reposition
    // punctuation within its translation (move a clause, merge/drop/replace a comma or full stop,
    // reorder a parenthetical), so splitting the sentence into separate fragments around punctuation
    // and reassembling with a fixed literal mark in between risks an ungrammatical or nonsensical
    // result. Sending the whole punctuated sentence as one fragment lets the LLM translate and
    // re-punctuate it naturally as a unit. (Plain ASCII punctuation - ',', '?', '!', '.', '-', etc. -
    // is intentionally NOT absorbed: in this data ASCII punctuation only ever appears as genuine
    // structural/game-syntax separators - list items, role logic, method calls - never as natural
    // Chinese sentence punctuation, so it should keep acting as a fragment boundary.)
    // The fullwidth colon '：' (U+FF1A) is the one exception carved out of the "Halfwidth and
    // Fullwidth Forms" block: unlike other CJK punctuation, a colon here consistently introduces an
    // enumerated/named item (e.g. "...小有名气的门派：仙霞派。" naming a specific sect, or
    // "<b>仙霞</b>：所有经验获取+5%。" naming a specific effect) where the text before the colon is
    // usually irrelevant context and the text after it is the actual payload that benefits from
    // being its own fragment/template boundary (e.g. "{0}：{1}"), rather than being folded into one
    // run and re-translated as a single sentence every time.
    // Runs made up of digits/signs/percent/punctuation only (no Chinese at all) are filtered out
    // afterwards and left as literal template text - e.g. "威望+10" still splits into fragment
    // "威望" + literal "+10" because nothing follows the number, and "1000-12-0-0" stays fully
    // literal.
    // Curly quotation marks ('“' '”' '‘' '’', U+2018/2019/201C/201D) are also absorbed even though
    // they live in the Unicode "General Punctuation" block rather than "CJK Symbols and
    // Punctuation"/"Halfwidth and Fullwidth Forms" - this game's text uses them as ordinary Chinese
    // quotation marks (e.g. wrapping a quoted word inside a sentence), so without this they act as
    // a spurious fragment boundary and split a quoted word out of its sentence, e.g.
    // "...摊开，都翻到小数字为“一”的那一页）" would otherwise split into three fragments around "一"
    // instead of staying one continuous sentence fragment.
    // Character class subtraction ('-[\uFF1A]') carves the fullwidth colon back out of
    // \p{IsHalfwidthandFullwidthForms} so it acts as a fragment boundary instead of being absorbed -
    // see the comment above.
    private const string CjkTextChars = @"\p{IsCJKUnifiedIdeographs}0-9.\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}\u2018\u2019\u201C\u201D-[\uFF1A]";

    [GeneratedRegex(@"(?:[+\-](?=[0-9]))?[" + CjkTextChars + @"]+(?:%[" + CjkTextChars + @"]*)*", RegexOptions.Compiled)]
    private static partial Regex TranslatableRunRegex();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}", RegexOptions.Compiled)]
    private static partial Regex ChineseCharRegex();

    // Caches one compiled run-regex per distinct CompoundFieldSplitterOptions instance whose
    // PlaceholderPatterns fold placeholder tokens directly into the run-matching alternation (see
    // GetTranslatableRunRegex/BuildRunRegexForOptions below) - callers are expected to reuse a
    // single options instance across many Decompose calls (e.g. one per game/config), so this
    // avoids recompiling the regex per cell while still supporting per-options patterns.
    private static readonly ConditionalWeakTable<CompoundFieldSplitterOptions, Regex> RunRegexCache = new();

    /// <summary>
    /// Returns the regex used to find translatable runs for the given options. When
    /// <paramref name="options"/> has no <see cref="CompoundFieldSplitterOptions.PlaceholderPatterns"/>
    /// configured, this is just the static <see cref="TranslatableRunRegex"/>. Otherwise a regex is
    /// built (and cached per options instance) where each placeholder pattern is folded in as just
    /// another alternative a run can extend through, alongside individual CJK/fullwidth characters -
    /// so a placeholder token sitting between/adjacent to Chinese text becomes part of the *same*
    /// regex match as that text, rather than a separate literal gap that has to be merged back in
    /// after the fact. This is what lets something like "...一方。\n#PlayerName#若是..." correctly
    /// stop at the literal "\n" (not part of any alternative) while still treating
    /// "#PlayerName#若是..." as one continuous run (the placeholder is just another alternative the
    /// run can pass through, immediately followed by more Chinese).
    /// </summary>
    private static Regex GetTranslatableRunRegex(CompoundFieldSplitterOptions options)
    {
        if (options.PlaceholderPatterns.Count == 0)
            return TranslatableRunRegex();

        return RunRegexCache.GetValue(options, BuildRunRegexForOptions);
    }

    private static Regex BuildRunRegexForOptions(CompoundFieldSplitterOptions options)
    {
        var alternatives = options.PlaceholderPatterns
            .Select(pattern => $"(?:{pattern})")
            .Append($"[{CjkTextChars}]");
        var core = $"(?:{string.Join('|', alternatives)})";

        return new Regex($@"(?:[+\-](?=[0-9]))?{core}+(?:%{core}*)*", RegexOptions.Compiled);
    }

    public static string[] ParseCsvRow(string line)
    {
        if (string.IsNullOrEmpty(line))
            return [];

        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        fields.Add(current.ToString());
        return [.. fields];
    }

    public static string RebuildCsvRow(IEnumerable<string> fields)
    {
        var rebuilt = fields.Select(field =>
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
                return $"\"{field.Replace("\"", "\"\"")}\"";

            return field;
        });

        return string.Join(',', rebuilt);
    }

    /// <summary>
    /// Extracts every maximal run of Chinese text out of a cell, replacing each with a positional
    /// "{n}" placeholder. Digits/decimal points directly glued to Chinese (e.g. "500" in
    /// "击败500人") stay part of the same fragment, a signed number or percentage that is glued to
    /// adjacent Chinese (e.g. "-99" in "-99表示自动", "50%" in "同盟区域50%后进入门派") is kept
    /// together with that surrounding text as one fragment too, and any full-width/CJK punctuation
    /// ('，' '。' '？' '！' '：' '；' '、' '（）' etc.) within a sentence does not break it into
    /// separate fragments, since punctuation position can legitimately move during translation and
    /// splitting around it plus reassembling with a fixed literal mark risks an ungrammatical
    /// result. Fragments that end up directly touching in the template with nothing structurally
    /// between them (e.g. a signed number continuing straight after CJK punctuation absorbed into
    /// the previous run, such as "占领门派（" immediately followed by "-99表示自动）") are merged
    /// back into a single fragment - there is no game-syntax boundary between them, so they should
    /// be sent to the LLM as one continuous piece of text rather than as two fragments each holding
    /// half of an unbalanced bracket. Everything else (delimiters, ids, method names, standalone
    /// numbers with nothing adjacent, ASCII structural punctuation used as genuine game-syntax
    /// separators, role tokens like '|'/'&amp;') is left untouched in the template. Returns an
    /// empty fragment list when there's no Chinese text (caller should treat the cell as a plain,
    /// non-compound column). If <paramref name="options"/> supplies
    /// <see cref="CompoundFieldSplitterOptions.PlaceholderPatterns"/>, each pattern is folded
    /// directly into the run-matching regex as another character a run can pass through (see
    /// <see cref="GetTranslatableRunRegex"/>) - a placeholder token immediately adjacent to Chinese
    /// text on either side becomes part of that same continuous run/fragment, instead of acting as
    /// a fixed literal split point pinned between two independently-translated fragments. A
    /// placeholder with no Chinese text anywhere in its run (nothing to glue onto) is left as plain
    /// literal template text, same as any other non-Chinese run.
    /// </summary>
    public static (string Template, List<string> Fragments) Decompose(string cell, CompoundFieldSplitterOptions? options = null)
    {
        var fragments = new List<string>();

        if (string.IsNullOrEmpty(cell))
            return (cell, fragments);

        // Escape any literal '{'/'}' already present in the cell - e.g. this game's own
        // String.Format placeholders (like "{0}年{1}月{2}日" or "{0}存档成功！") surviving
        // verbatim in a DynamicStringsIL2CPP dump line - to '⟦'/'⟧' (U+27E6/U+27E7, "mathematical
        // white square brackets") before generating our own "{n}" fragment placeholders below.
        // Without this, a literal "{0}" already in the source text is byte-for-byte
        // indistinguishable from a synthesized fragment placeholder once both land in the same
        // template string, so Reconstruct's per-index string.Replace($"{{{i}}}", ...) stomps the
        // literal occurrence too (or vice versa) - confirmed to turn "{0}年{1}月{2}日" into
        // "年年月月日日" and "{0}存档成功！" into a duplicated "存档成功！存档成功！". (Doubling the
        // ASCII braces instead, e.g. "{0}" -> "{{0}}", does NOT work - "{{0}}" still *contains*
        // the literal substring "{0}", so Reconstruct's plain substring replace would still
        // corrupt it. Fullwidth brace lookalikes, e.g. '｛'/'｝', don't work either - they fall
        // inside \p{IsHalfwidthandFullwidthForms}, which TranslatableRunRegex/BuildRunRegexForOptions
        // below deliberately absorb into CJK runs, so they'd get swallowed into the fragment text
        // itself instead of staying a literal boundary marker.) '⟦'/'⟧' sit outside every Unicode
        // block CjkTextChars absorbs, stay visually obvious as "this used to be a brace" in
        // exported YAML (unlike an invisible sentinel character), and can never collide with an
        // ASCII "{n}" placeholder search since they're entirely different code points - they're
        // restored to real ASCII braces by <see cref="Reconstruct"/> only after fragment
        // substitution has happened.
        var escapedCell = cell.Replace("{", "⟦").Replace("}", "⟧");

        var runRegex = GetTranslatableRunRegex(options ?? CompoundFieldSplitterOptions.Default);
        var rawTemplate = runRegex.Replace(escapedCell, match =>
        {
            if (!ChineseCharRegex().IsMatch(match.Value))
                return match.Value; // pure quote/paren/digit/decimal-point run - not translatable, leave as-is

            var placeholder = $"{{{fragments.Count}}}";
            fragments.Add(match.Value);
            return placeholder;
        });

        var (rebalancedTemplate, rebalancedFragments) = RebalanceBoundaryMarks(rawTemplate, fragments);

        return MergeAdjacentFragments(rebalancedTemplate, rebalancedFragments);
    }

    // Boundary quote/bracket pairs that this game's text uses to wrap a quoted word, title, or
    // (for fullwidth parens) a parenthetical aside - see RebalanceBoundaryMarks below for why a
    // pair needs special handling when a placeholder sits between its two halves.
    private static readonly (char Open, char Close)[] BoundaryMarkPairs =
    [
        ('\u201C', '\u201D'), // “ ”
        ('\u2018', '\u2019'), // ‘ ’
        ('\u300A', '\u300B'), // 《 》
        ('\uFF08', '\uFF09'), // （ ）
    ];

    /// <summary>
    /// Fixes a specific mis-split that happens when a game-format placeholder (escaped to "⟦n⟧" -
    /// see Decompose above) sits directly between the two halves of a quote/bracket pair that are
    /// BOTH present in the same raw cell, e.g. raw "“{0}”竟有这等境界...\n我不能及也": the opening
    /// "“" has no adjacent Chinese to join (the escaped placeholder isn't part of any translatable
    /// run), so it becomes an isolated literal token on its own - but the closing "”" IS directly
    /// adjacent to real Chinese text right after it ("之高，"/"竟有这等境界..."), so the regex
    /// naturally absorbs it into the START of that fragment instead, e.g. producing fragment text
    /// "”竟有这等境界...". Left alone, the reconstructed template would end up with only the
    /// OPENING mark as a template literal while the CLOSING mark rides along inside the translated
    /// fragment - i.e. the pair gets torn apart even though the original text is fully
    /// self-contained (both marks belong to the same clause, framing the placeholder, not
    /// continuing into a different row/split the way <see cref="LineValidation.FixUnbalancedQuotes"/>/
    /// <see cref="LineValidation.FixUnbalancedParentheses"/> handle for a GENUINELY one-sided
    /// fragment). The fix: when a literal token contains an unmatched open mark (no closing mark
    /// already balancing it later in that same literal) immediately followed by a fragment whose
    /// text starts with the matching close mark, move that leading close mark out of the fragment
    /// and onto the end of the literal - restoring the original "open⟦0⟧close" literal shape and
    /// leaving the fragment with only the genuinely translatable text after it. Keeps the original
    /// CJK glyph (no ASCII conversion) since this literal text is never sent to the LLM at all.
    /// </summary>
    private static (string Template, List<string> Fragments) RebalanceBoundaryMarks(string rawTemplate, List<string> rawFragments)
    {
        if (rawFragments.Count == 0)
            return (rawTemplate, rawFragments);

        var tokens = Tokenize(rawTemplate, rawFragments);

        foreach (var (open, close) in BoundaryMarkPairs)
        {
            for (int k = 0; k < tokens.Count - 1; k++)
            {
                if (tokens[k].IsFragment || !tokens[k + 1].IsFragment)
                    continue;

                var literalText = tokens[k].Text;
                var fragmentText = tokens[k + 1].Text;

                if (fragmentText.Length == 0 || fragmentText[0] != close)
                    continue;

                var openIdx = literalText.LastIndexOf(open);
                if (openIdx < 0)
                    continue;

                // If a close already appears after that open within the same literal, this pair is
                // already balanced there - not our case (avoid double-moving an unrelated pair).
                if (literalText[(openIdx + 1)..].Contains(close))
                    continue;

                tokens[k] = (false, literalText + close);
                tokens[k + 1] = (true, fragmentText[1..]);
            }
        }

        return Rebuild(tokens);
    }

    /// <summary>
    /// Splits a template string (with "{n}" fragment placeholders) and its ordered fragment list
    /// into an alternating sequence of literal/fragment tokens - always starting and ending with a
    /// literal token (possibly empty), so every fragment has a literal neighbour on each side to
    /// inspect. Shared by <see cref="RebalanceBoundaryMarks"/> and <see cref="MergeAdjacentFragments"/>.
    /// </summary>
    private static List<(bool IsFragment, string Text)> Tokenize(string template, IReadOnlyList<string> fragments)
    {
        var tokens = new List<(bool IsFragment, string Text)>();
        var literal = new StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                tokens.Add((false, literal.ToString()));
                literal.Clear();

                var close = template.IndexOf('}', i);
                var index = int.Parse(template[(i + 1)..close]);
                tokens.Add((true, fragments[index]));
                i = close + 1;
            }
            else
            {
                literal.Append(template[i]);
                i++;
            }
        }
        tokens.Add((false, literal.ToString()));

        return tokens;
    }

    /// <summary>
    /// Reassembles a token list (see <see cref="Tokenize"/>) back into a (template, fragments)
    /// pair, renumbering "{n}" placeholders to match the fragment list's final order.
    /// </summary>
    private static (string Template, List<string> Fragments) Rebuild(List<(bool IsFragment, string Text)> tokens)
    {
        var fragments = new List<string>();
        var template = new StringBuilder();
        foreach (var token in tokens)
        {
            if (token.IsFragment)
            {
                template.Append('{').Append(fragments.Count).Append('}');
                fragments.Add(token.Text);
            }
            else
            {
                template.Append(token.Text);
            }
        }

        return (template.ToString(), fragments);
    }




    /// <summary>
    /// Merges fragments that end up directly touching in the template with an empty literal gap
    /// between them. This happens when the leading sign/digit lookahead in
    /// <see cref="TranslatableRunRegex"/> (or its per-options equivalent from
    /// <see cref="GetTranslatableRunRegex"/>) restarts a new match immediately where the previous
    /// one ended, with no boundary character actually consumed in between - e.g. "占领门派（" ends
    /// a match right before '-', and "-99表示自动）" begins a new match starting with that same
    /// '-' (via the sign lookahead), leaving nothing at all between the two matches. There was
    /// never a real game-syntax separator there, so the two fragments must be treated as one
    /// continuous piece of translatable text rather than two fragments each holding half of an
    /// unbalanced bracket. (Placeholder tokens no longer need handling here - they're folded
    /// directly into the run-matching regex itself, so a placeholder adjacent to Chinese text is
    /// already part of the same match/fragment by the time this runs; see
    /// <see cref="GetTranslatableRunRegex"/>.)
    /// </summary>
    private static (string Template, List<string> Fragments) MergeAdjacentFragments(
        string rawTemplate, List<string> rawFragments)
    {
        if (rawFragments.Count == 0)
            return (rawTemplate, rawFragments);

        var tokens = Tokenize(rawTemplate, rawFragments);

        // Repeatedly fuse two fragments together whenever an empty literal token sits between them
        // until no more merges apply.
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int k = 0; k < tokens.Count; k++)
            {
                if (tokens[k].IsFragment || tokens[k].Text.Length != 0)
                    continue;

                bool prevIsFragment = k > 0 && tokens[k - 1].IsFragment;
                bool nextIsFragment = k < tokens.Count - 1 && tokens[k + 1].IsFragment;

                if (prevIsFragment && nextIsFragment)
                {
                    tokens[k - 1] = (true, tokens[k - 1].Text + tokens[k + 1].Text);
                    tokens.RemoveRange(k, 2);
                    changed = true;
                    break;
                }
            }
        }

        return Rebuild(tokens);
    }


    /// <summary>
    /// True when a decomposed cell is nothing but a single fragment spanning the whole cell (no
    /// surrounding structure at all, i.e. template is exactly "{0}"). Callers can use this to skip
    /// recording a <see cref="Support.FieldTemplate"/> for such trivial cases and instead treat the
    /// column like a plain whole-cell split, avoiding template noise in the exported YAML.
    /// </summary>
    public static bool IsTrivialTemplate(string template, int fragmentCount) =>
        fragmentCount == 1 && template == "{0}";

    // Normalizes bare quote/bracket/paren marks left as literal (non-fragment) template text to
    // the same ASCII style the translated side gets normalized to elsewhere in the pipeline - see
    // the call site in Decompose above for why a literal-only run needs this (it never goes
    // through <see cref="LineValidation.PrepareRaw"/>/translation/cleanup otherwise, so it would
    // keep its original CJK glyph forever while its counterpart - if it instead ends up inside a
    // translated fragment - gets normalized). Two independent normalizations apply here, matching
    // the two places that already do this on the fragment side:
    //  - Curly double quotes ("“"/"”"), curly single quotes ("‘"/"’"), and CJK title brackets
    //    ("《"/"》") all consistently map to a single ASCII "'", matching
    //    <see cref="LineValidation.FixUnbalancedQuotes"/>'s target style for a translated
    //    fragment's boundary quote.
    //  - Fullwidth parentheses ("（"/"）") map to their ASCII equivalents "("/")", matching
    //    <see cref="LineValidation.PrepareRaw"/>'s normalization (applied to fragment text before
    //    it's ever sent to the LLM) and <see cref="LineValidation.FixUnbalancedParentheses"/>'s
    //    ASCII-only boundary-paren repair on the translated side. Without this, a stranded literal
    //    "（" (e.g. isolated from its matching "）" by an escaped format-placeholder marker
    //    sitting in between) would survive verbatim in the packaged output as a fullwidth paren
    //    while its counterpart got repaired/normalized to an ASCII paren - the exact same
    //    mismatched-pair bug class as the quote case above.
    private static string NormalizeLiteralQuoteMarks(string s) =>
        s.Replace('\u201C', '\'').Replace('\u201D', '\'')  // “ ”
         .Replace('\u2018', '\'').Replace('\u2019', '\'')  // ‘ ’
         .Replace('\u300A', '\'').Replace('\u300B', '\'')  // 《 》
         .Replace('\uFF08', '(').Replace('\uFF09', ')');   // （ ）

    /// <summary>
    /// Rebuilds a cell from its template and translated fragments, in fragment order.
    /// </summary>
    public static string Reconstruct(string template, IReadOnlyList<string> translatedFragments)
    {
        if (translatedFragments.Count == 0)
            return UnescapeBraces(template);

        var result = template;
        for (int i = 0; i < translatedFragments.Count; i++)
            result = result.Replace($"{{{i}}}", translatedFragments[i]);

        return UnescapeBraces(result);
    }

    // Reverses the '⟦'/'⟧' escaping Decompose applies to literal '{'/'}' characters, restoring
    // the original ASCII braces now that fragment placeholder substitution is done and there's no
    // more risk of confusing the two.
    private static string UnescapeBraces(string s) =>
        s.Replace("⟦", "{").Replace("⟧", "}");
}
