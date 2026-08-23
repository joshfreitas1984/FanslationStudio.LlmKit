---
applyTo: "**"
---

# FanslationStudio.LlmKit — Copilot Instructions

Shared library that implements a **Line → Splits** data pipeline for extracting Chinese text out
of game data files (CSV rows, dynamic strings, etc.), sending it through an LLM for translation,
and reassembling the translated result back into the original file format without corrupting
structure. It is consumed by downstream "over LLM" translation projects (e.g. `DragonHeirOverLlm`)
via a project reference to `FanslationStudio.LlmKit.csproj`.

> **Workflow rule:** After any significant feature or fix, update this file and `readme.md` —
> they are the primary source of truth for future sessions.

## Core data model (`Support/`)

- `TranslationLine` — one raw line/row from the source file.
  - `Raw` (string) — the original untouched line.
  - `Splits` (`List<TranslationSplit>`) — the translatable fragments extracted from `Raw`.
  - `Templates` (`List<FieldTemplate>`) — reconstruction info for compound columns (see below).
  - `Translated` (`[YamlIgnore]`) — computed at packaging time, never serialized.
- `TranslationSplit` — one translatable fragment.
  - `Split` (int) — CSV column index (or logical field index) this fragment came from.
  - `SubIndex` (int, default `0`) — position of this fragment *within* its column, when a column
    was decomposed into multiple fragments. Zero for plain single-fragment columns — **do not**
    assume `SubIndex == 0` means "the only fragment"; always filter by `Split` first.
  - `Text` / `Translated` — source and translated text.
  - `SafeToTranslate`, `FlaggedForRetranslation`, `FlaggedMistranslation`, `FlaggedHallucination` —
    workflow flags consumed by `Workflow/TranslationWorkflow.cs` and `TranslationService`.
- `FieldTemplate` — `{ Split, Template }`. Only present for columns that contain **more than one**
  translatable fragment mixed with structural characters (e.g. `;`, `-`, `&`, `|`, method names,
  ids). The `Template` string holds the original cell with each fragment replaced by `{n}`.
  **Trivial single-fragment whole-cell columns must never get a `FieldTemplate` entry** — this
  would just add `{0}`-only noise to the exported YAML. Use
  `CompoundFieldSplitter.IsTrivialTemplate` to detect and skip these before adding a template.
- `TextFileToSplit.SkipColumns` (`HashSet<int>`, in `Support/TextFileToSplit.cs`) — zero-based
  column indices for a given file that should **never** be decomposed/translated at all, even if
  they happen to contain CJK characters (e.g. an icon/resource-path column). This is a per-file
  opt-out the consuming project configures on its `TextFileToSplit` entries — the shared library
  itself has no opinion on which columns are translatable for any given game/file. A skipped
  column is never passed to `CompoundFieldSplitter.Decompose` at all, so it doesn't matter whether
  its content would otherwise have produced one fragment or several sub-fragments — no
  `TranslationSplit`/`FieldTemplate` is ever created for it, and it round-trips verbatim from the
  original raw CSV on export/packaging.

**Golden rule:** the Line → Splits → (Templates) hierarchy is the contract every downstream
project depends on. When adding new capability, extend it with new optional fields (with safe
defaults) rather than changing its shape — old serialized YAML must keep deserializing correctly.

## CSV / compound field parsing (`Utility/CompoundFieldSplitter.cs`)

Game CSV rows must be parsed as **real CSV**, not `line.Split(',')` — cells are frequently
double-quoted and contain embedded commas, e.g. the BuildingData action column. Use
`CompoundFieldSplitter.ParseCsvRow` / `RebuildCsvRow` for all row parsing/rebuilding; they handle
quote-escaping (`""`) and only re-quote a field on rebuild if it actually needs it (contains `,`,
`"`, or a newline).

`CompoundFieldSplitter.Decompose(cell)` extracts translatable fragments from a single cell:

- Matches runs via `(?:[+\-](?=[0-9]))?[<CjkTextChars>]+(?:%[<CjkTextChars>]*)*` where
  `<CjkTextChars>` = `\p{IsCJKUnifiedIdeographs}0-9.\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}`,
  then **discards** any matched run that turns out to be pure digits/sign/percent/punctuation with
  no actual Chinese character (that's just a number/format string and is left as literal template
  text, e.g. `1000-12-0-0/1/2/3/4/5`, or `威望+10` where `+10` has nothing following it to glue to).
- **Digits/decimal points glued directly to Chinese with no separator stay in the same fragment**
  — e.g. `累计在战斗中亲手击败500人` must decompose to **one** fragment
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
- **Any full-width/CJK punctuation (`，` `。` `？` `！` `：` `；` `、` `（）` `～` etc. — i.e. the
  Unicode "CJK Symbols and Punctuation" and "Halfwidth and Fullwidth Forms" blocks) is absorbed
  into the run and never acts as a fragment boundary.** An LLM is free to reposition, merge, or
  drop punctuation during translation (move a clause, reorder a parenthetical, change a comma to a
  full stop), so splitting a sentence into separate fragments around its own internal punctuation
  and reassembling with a fixed literal mark in between risks an ungrammatical or nonsensical
  result. **Plain ASCII punctuation (`,`, `?`, `!`, `-` not before a digit, etc.) is intentionally
  NOT absorbed** — in this game's data ASCII punctuation only ever appears as a genuine
  structural/game-syntax separator (list items via `;`, role logic via `&`/`|`, method calls via
  `--MethodName`), never as natural Chinese sentence punctuation, so it must keep acting as a
  boundary. Do not extend the absorbed set to ASCII punctuation without first confirming a
  concrete case where ASCII punctuation is genuinely natural-language (not game syntax).
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
- Full-width/CJK punctuation (`，。？！：；、（）` etc.) — never a boundary; always part of natural
  sentence text and stays glued to whichever fragment it's adjacent to.

### Game-specific placeholder tokens (`CompoundFieldSplitterOptions`)

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

Any regex in `PlaceholderPatterns` that **fully** matches the literal text sitting between two
fragments (or leading/trailing a single fragment) causes that gap to be absorbed into the
adjacent fragment(s) rather than left as a fixed boundary — including fusing two fragments into
one when the placeholder sits directly between them (mirrors the existing zero-gap merge, just
generalized to "gap is empty OR gap is purely a placeholder match"). Omitting `options` (or using
`CompoundFieldSplitterOptions.Default`) preserves the original game-agnostic behavior where every
ASCII character between two Chinese runs is a hard boundary. See `DragonHeirOverLlm`'s
`Tests/GameFileHandling.cs` for the concrete `#PlayerName#` configuration for that game.

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

## Testing conventions

- Prefer pure, fast unit tests against static utility methods (e.g. `CompoundFieldSplitter`) over
  running the file-based workflow tests, which mutate real working-directory state
  (`Files/Raw/Export`, `Files/Converted`, `Files/Mod`) and are meant to drive an actual translation
  run, not to be used as CI-style regression tests.
- When fixing a bug in fragment extraction/reconstruction, add a targeted xUnit test asserting the
  exact `Template`/`Fragments` shape rather than only checking round-trip equality — round-tripping
  alone won't catch "sentence split around an embedded number" style regressions.
