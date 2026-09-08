# `CompoundFieldSplitter` — regex design history and rationale

> Extracted from `.github/copilot-instructions.md` during the Sep 2026 docs restructuring (see
> `AGENTS.md`'s workflow rule) to keep the instructions file to current-state rules only. This file
> holds the *why* behind the current regex/absorption design; the instructions file holds the
> current *what* (the short summary of what's absorbed vs. not).

## Full character-run matching design

`CompoundFieldSplitter.Decompose(cell)` extracts translatable fragments from a single cell:

- Matches runs via `(?:[+\-](?=[0-9]))?[<CjkTextChars>]+(?:%[<CjkTextChars>]*)*` where
  `<CjkTextChars>` =
  `\p{IsCJKUnifiedIdeographs}0-9.\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}‘’“”-[：]`
  (the trailing `-[：]` is .NET regex character-class subtraction, carving the fullwidth colon
  `：` back OUT of `\p{IsHalfwidthandFullwidthForms}` — see the colon bullet below), then
  **discards** any matched run that turns out to be pure digits/sign/percent/punctuation with no
  actual Chinese character (that's just a number/format string and is left as literal template
  text, e.g. `1000-12-0-0/1/2/3/4/5`, or `威望+10` where `+10` has nothing following it to glue to).
- **Digits/decimal points glued directly to Chinese with no separator stay in the same fragment** —
  e.g. `累计在战斗中亲手击败500人` must decompose to **one** fragment
  (`累计在战斗中亲手击败500人`), not `{0}500{1}`. Splitting a sentence around an embedded number
  produces worse LLM translations because the model loses sentence context.
- **A leading sign (`+`/`-`) directly before a digit is absorbed into the fragment when that
  number is itself glued onward into more Chinese** — e.g. the `-99` in `占领门派（-99表示自动）`
  must never be left stranded outside a fragment (i.e. never `{0}（-{1}）` with only `99表示自动`
  sent to the LLM) — a bare number can be reordered/dropped by the LLM and silently break a
  sentinel value. The sign is only absorbed when immediately followed by a digit
  (`(?=[0-9])` lookahead) — it does **not** trigger on non-numeric text.
- **A trailing `%` is absorbed the same way, and matching then keeps extending through further
  CJK/digit text after it** — e.g. `同盟区域50%后进入门派/自宅` decomposes to fragments
  `同盟区域50%后进入门派` and `自宅` (template `{0}/{1}`), not split at the `%`.
- **Any full-width/CJK punctuation (`，` `。` `？` `！` `；` `、` `（）` `～` etc. — i.e. the
  Unicode "CJK Symbols and Punctuation" and "Halfwidth and Fullwidth Forms" blocks) is absorbed
  into the run and never acts as a fragment boundary — EXCEPT the fullwidth colon `：` (U+FF1A),
  fixed Aug 2026 (see `FullwidthColonActsAsFragmentBoundary` in
  `Tests/CompoundFieldSplitterTests.cs`): unlike other CJK punctuation, a colon consistently
  introduces a specific named/enumerated payload (e.g. `正是于巴蜀一带小有名气的门派：仙霞派。`
  naming a sect, or `<b>仙霞</b>：所有经验获取+5%。` naming an effect) where the text after it
  benefits from being its own fragment/template boundary (`{0}：{1}`) rather than being folded into
  one whole-sentence fragment every time — so it's carved out of `CjkTextChars` via character-class
  subtraction instead of being absorbed like every other CJK punctuation mark. Curly Chinese
  quotation marks `“` `”` `‘` `’` (`U+201C`/`U+201D`/`U+2018`/`U+2019`) are **also** explicitly
  absorbed even though they live in the Unicode "General Punctuation" block, not the two CJK blocks
  above — this game uses them as ordinary in-sentence quotation marks (e.g.
  `...摊开，都翻到小数字为"一"的那一页）`), and without the explicit addition they acted as a
  spurious boundary, splitting a quoted word out into its own fragment mid-sentence (a real bug
  fixed Aug 2026 — see the `CurlyChineseQuotationMarksStayGluedIntoSurroundingSentence` test in
  `Tests/CompoundFieldSplitterTests.cs`). An LLM is free to reposition, merge, or drop punctuation
  during translation (move a clause, reorder a parenthetical, change a comma to a full stop), so
  splitting a sentence into separate fragments around its own internal punctuation and reassembling
  with a fixed literal mark in between risks an ungrammatical or nonsensical result. **Plain ASCII
  punctuation (`,`, `?`, `!`, `-` not before a digit, etc.) is intentionally NOT absorbed** — in
  this game's data ASCII punctuation only ever appears as a genuine structural/game-syntax
  separator (list items via `;`, role logic via `&`/`|`, method calls via `--MethodName`), never as
  natural Chinese sentence punctuation, so it must keep acting as a boundary. Do not extend the
  absorbed set to ASCII punctuation without first confirming a concrete case where ASCII
  punctuation is genuinely natural-language (not game syntax).
- **After matching, adjacent placeholders that end up directly touching in the template with zero
  characters between them are merged back into one fragment** (see `MergeAdjacentFragments`). This
  only happens when the leading-sign lookahead restarts a match immediately where the previous one
  ended (e.g. CJK punctuation absorbed into one run, then a sign+digit immediately following it,
  such as `占领门派（` + `-99表示自动）`) — there was never a real structural separator there, so
  the two runs must be sent to the LLM as a single continuous piece of text, not as two fragments
  each holding half of an unbalanced bracket. This makes `占领门派（-99表示自动）` decompose to a
  single fragment identical to the whole cell (template `{0}`).
- Everything else (delimiters, ids, method names, standalone numeric fields, role tokens `|`/`&`)
  is left untouched in the template string.
- Returns an empty fragment list when there is no Chinese at all — caller should skip creating any
  split/template for that column (nothing to translate).

`CompoundFieldSplitter.Reconstruct(template, translatedFragments)` rebuilds the cell by
substituting `{0}`, `{1}`, ... in order — never rebuild compound cells by hand.

Known game-data compound patterns worth recognizing when reasoning about `Decompose` output:
- `;` — separates a list of items within one cell (e.g. multiple building actions).
- `-` — separates role/method metadata from the action payload within one item, **except** when
  it appears inside a plain numeric field (leave those as literal, e.g. `1000-12-0-0`) or directly
  before a digit glued to surrounding Chinese (a negative number, e.g. `-99表示自动`).
- `&` — "AND" role requirement (multiple required roles).
- `|` — "OR" role requirement (alternative roles).
- `/` — list of numeric values inside a compound numeric sub-field, or a genuine ASCII clause
  boundary between two otherwise-unrelated sentences (e.g. `.../自宅`).
- Full-width/CJK punctuation (`，。？！；、（）` etc.) — never a boundary; always part of natural
  sentence text and stays glued to whichever fragment it's adjacent to. **Exception: the fullwidth
  colon `：` IS a boundary** (see above) — it always splits into a `{0}：{1}`-shaped template.

## Game-specific placeholder tokens (`CompoundFieldSplitterOptions`)

**The shared library has no hardcoded knowledge of any one game's placeholder syntax.** Games
commonly wrap a dynamic value (player name, item name, etc.) in a marker token — e.g.
`#PlayerName#` — whose *position* can legitimately move during translation (the name might need to
shift to the front/back of the sentence in the target language). If such a token is left as fixed
literal template text sitting between two independently-translated fragments, that position is
pinned and can produce an ungrammatical result — this is exactly the same class of problem that
full-width punctuation absorption solves for natural punctuation, just for a game-specific token.

Rather than baking in a rule like "`#...#` is always a placeholder" (another game could just as
legitimately use `#` as a genuine structural separator instead), this is opted into **per game** by
passing a `CompoundFieldSplitterOptions` to `Decompose(cell, options)`:

```csharp
var options = new CompoundFieldSplitterOptions
{
    PlaceholderPatterns = [new Regex(@"#\w+#", RegexOptions.Compiled)]
};
var (template, fragments) = CompoundFieldSplitter.Decompose(cell, options);
```

Any regex in `PlaceholderPatterns` is folded directly into the run-matching regex itself as just
another alternative a run can extend through (see `GetTranslatableRunRegex`/`BuildRunRegexForOptions`
in `CompoundFieldSplitter.cs`) — a placeholder token immediately adjacent to Chinese text on either
side becomes part of the *same* regex match/fragment as that text, rather than a separate literal
gap that has to be merged back in after the fact. Omitting `options` (or using
`CompoundFieldSplitterOptions.Default`) preserves the original game-agnostic behavior where every
ASCII character between two Chinese runs is a hard boundary. See `DragonHeirOverLlm`'s
`Tests/GameFileHandling.cs` for the concrete `#PlayerName#` configuration for that game.

**Design history (Aug 2026) — folded into the run regex instead of post-hoc gap merging.** An
earlier version tried to detect and merge "non-structural gaps" (placeholder matches, isolated CJK
punctuation) *after* the initial regex pass, via `MergeAdjacentFragments`/`IsMergeableGap`. That
approach kept missing composite cases — e.g. `#PlayerName#！#PlayerName#都...` (a punctuation mark
stranded *between* two placeholders) and `...一方。\n#PlayerName#若是...` (a placeholder sitting
*right after* a genuine literal boundary like `\n`, where only part of the "gap" should merge) —
because gap-merging only ever considered a whole literal span between two `{n}` tokens as one
unit, either merging all of it or none. The fix was to stop treating placeholders as a
post-processing concern entirely: `GetTranslatableRunRegex` builds (and caches, per
`CompoundFieldSplitterOptions` instance, via a `ConditionalWeakTable`) a regex where each
placeholder pattern is just another alternative inside the same repeating "core" group as the CJK
character class, e.g. `(?:(?:#\w+#)|[<CjkTextChars>])+`. This means a placeholder adjacent to
Chinese text is consumed by the *same* regex match as that text from the start — it can never end
up as a separate literal token in the first place, so nothing needs merging back in. Only the
sign/digit-restart empty-gap case (see below) still needs a post-pass, because that one is a
genuine artifact of two *separate* regex matches ending up with zero characters between them, not
a placeholder/punctuation concern. If you're tempted to add another kind of "gap that should
merge", check first whether it can instead be expressed as another alternative folded into
`BuildRunRegexForOptions`'s core group — that's almost always simpler and more correct than
detecting the gap afterward.

**A remaining post-pass only handles the sign/digit-restart empty-gap case** (`MergeAdjacentFragments`
in `CompoundFieldSplitter.cs`): `TranslatableRunRegex`'s leading `(?:[+\-](?=[0-9]))?` can restart a
new match immediately where a previous one ended (e.g. `占领门派（` ends one match right before `-`,
`-99表示自动）` begins the next), leaving an empty literal gap between the two `{n}` fragments —
those get fused into one fragment. This is unrelated to placeholders and still applies with or
without `CompoundFieldSplitterOptions`.

## Merging translations across re-exports (`GameFileHandlingBase.MergeFilesIntoTranslatedAsync`)

When re-exporting after a game update, splits must be matched between the old `Converted/*.yaml`
and the freshly exported `Raw/Export/*.yaml` so existing translations aren't lost. Matching order
matters now that one column can produce several fragments:

1. Try `(Split, SubIndex, Text)` match first — most precise, handles compound columns correctly.
2. Fall back to `Text`-only match — preserves backward compatibility with older exports that predate
   `SubIndex`/multi-fragment columns, and still works for plain single-fragment columns.

Do not regress to `Text`-only matching as the primary key — with multi-fragment columns this risks
cross-matching unrelated fragments that happen to share the same Chinese text (e.g. a common `我`
or `交易` fragment appearing in many different lines/columns).

## Known cost of the fragment model

Splitting a compound column into multiple fragments changes `TranslationSplit.Text` for that
column (whole-cell text → per-fragment text), so previously translated compound cells will not
auto-match on export/merge and will need re-translation once. This is expected and acceptable —
it only affects columns that actually contain multiple fragments (compound columns), not plain
single-value columns.
