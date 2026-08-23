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
>
> **Reverse-engineering rule:** Whenever you investigate/reverse-engineer how existing code in
> this repo works (tracing a log line back to its source, figuring out why a validation heuristic
> fires, mapping a runtime behavior back to the responsible method, etc.), write down what you
> learned in this file before finishing the task, even if not explicitly asked to document it —
> findings that only exist in chat history are lost for future sessions. Keep entries concise and
> reference exact file/method names so a future session can jump straight to the relevant code.
> Consuming repos (e.g. `DragonHeirOverLlm`) should record LlmKit-internal findings here, not in
> their own repo notes, since this is the sibling repo the logic actually belongs to.

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
  `<CjkTextChars>` =
  `\p{IsCJKUnifiedIdeographs}0-9.\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}\u2018\u2019\u201C\u201D`,
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
  into the run and never acts as a fragment boundary.** Curly Chinese quotation marks `“` `”` `‘`
  `’` (`U+201C`/`U+201D`/`U+2018`/`U+2019`) are **also** explicitly absorbed even though they live
  in the Unicode "General Punctuation" block, not the two CJK blocks above — this game uses them as
  ordinary in-sentence quotation marks (e.g. `...摊开，都翻到小数字为"一"的那一页）`), and without
  the explicit addition they acted as a spurious boundary, splitting a quoted word out into its own
  fragment mid-sentence (a real bug fixed Aug 2026 — see the
  `CurlyChineseQuotationMarksStayGluedIntoSurroundingSentence` test in
  `Tests/CompoundFieldSplitterTests.cs`). An LLM is free to reposition, merge, or
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

## Translation performance / retry / "Unprocessable" notes (`TranslationService.cs`)

- Backend is typically a local Ollama instance (`http://localhost:11434/api/chat`), which usually
  serves one request at a time per model regardless of client-side concurrency — setting a high
  `maxConcurrency` in a consuming project's config does not give proportional real throughput
  against that backend; requests just queue up.
- `TranslateSplitAsync` already retries a failing split fully internally: up to
  `LlmConfig.RetryCount` whole-cell attempts, each potentially followed by up to `RetryCount`
  sentence-by-sentence correction rounds (only entered when
  `ValidationResult.RequiresSentenceBySentenceCorrection` is set, e.g. leftover Chinese
  characters). **Never wrap another `RetryCount`-bounded retry loop around a call to
  `TranslateSplitAsync` (or anything that calls it)** — this squares the worst-case call count
  instead of adding to it. This was a real bug fixed in this method and in
  `CorrectSentenceBySentenceAsync`/`SplitBracketsRegexIfNeededAsync`'s `fullTrans` step; see git
  history/comments in `TranslationService.cs` around those call sites for the reasoning.
- `TranslateViaLlmAsyncPooled`'s progress log (`Processed: X of Y pending split(s) (Z unique
  total)...`) uses `pendingCount` (splits that still need translation, by the same condition used
  in the worker loop) as the denominator, not `workItems.Count` (every unique split including
  already-translated ones from a prior run) — using the raw count makes an already-mostly-done run
  look stuck.
- `incorrectLineCount` (logged as `Unprocessable`) is incremented once per work item, only after
  all its retries are exhausted and `split.Translated` ends up empty — it is a count of
  permanently-failed splits, not a running tally of individual retry failures. `CheckTransalationSuccessful`
  in `LineValidation.cs` is the validator whose heuristics (banned phrases, output-length
  hallucination checks, spurious punctuation insertion on short strings, missing/added
  tags/placeholders, leftover Chinese, etc.) decide whether a split needs a retry; a split can
  also become permanently unprocessable if `TranslateSplitAsync` catches an `HttpRequestException`
  (Ollama connection/timeout issue), which returns invalid immediately with no further retry.
- **Diagnostic logging (added Aug 2026):** `TranslateViaLlmAsyncPooled` now captures the raw text +
  failure reason (`ValidationResult.CorrectionPrompt`, or a placeholder noting a likely HTTP
  failure) for every split that ends up unprocessable, and writes them to
  `{workingDirectory}/TestResults/UnprocessableItems.log` periodically during the run (same
  cadence as the `Processed: ...` progress log, via the `WriteUnprocessableItemsLog` helper) as
  well as once more at the end — overwritten each write, not appended, so it always reflects the
  run so far rather than only being visible after the whole run finishes. Use this file to see
  which validation heuristic dominates real failures before tuning `LineValidation.cs` heuristics,
  `RetryCount`, or investigating Ollama-side timeouts/concurrency — don't guess from the aggregate
  `Unprocessable` count alone.
- **Leading structural punctuation is stripped and re-attached deterministically, not left to the
  model (added Aug 2026):** a real run's `UnprocessableItems.log` showed the single largest cause
  of unprocessable splits was raw text starting with a bare `：`/`:` (e.g.
  `：七十二洞研究奇门兵器，提升奇门威力。`) — a leftover field-templating artifact (the label half
  of a compound cell was already carved off elsewhere), never natural Chinese sentence punctuation.
  Forcing the LLM to preserve a bare leading separator like this in fluent English is unnatural, so
  it reliably drops it, which then fails `CheckTransalationSuccessful`'s "Removed :" check and
  burns a full retry budget every time for a mark that doesn't need the model's help at all.
  `TranslateSplitAsync` now has a dedicated branch (right after the existing
  `ColorTagHelpers.StartsWithHalfColorTag` branch, following the same "split off the part the
  model shouldn't need to handle, translate only the remainder, recombine ourselves" pattern): if
  `preparedRaw`'s first character is in `LeadingStructuralPunctuation` (`:`, `;`, `,`), it strips
  that character, recursively translates the remainder, and prepends the original character back
  onto the result itself — deterministic and retry-free, instead of hoping the model preserves it.
  This bypasses `CheckTransalationSuccessful` validation entirely for the combined result (same as
  the color-tag branch does), so don't extend this set to punctuation that might carry real
  meaning if attached to model-produced text without a review pass first.
- **Not every unprocessable split is fixable by a code/heuristic change** — the same real run's log
  also showed long, idiomatic/archaic wuxia-style sentences (e.g. `KungFuData.csv` flavor text)
  still ending up unprocessable after exhausting the leftover-Chinese sentence-correction retries.
  That's a genuine `qwen2.5:7b` capability limit on hard sentences, not a bug. `retryCount` was
  dropped from `3` to `1` in the consuming `DragonHeirOverLlm` repo's `Files/Config.yaml` for this
  reason — with only one real model available, extra whole-cell retry attempts against it mostly
  just re-prompt the same model and burn time rather than meaningfully improving the success rate.
- **Model escalation (implemented Aug 2026):** `LlmConfig.EscalationModelName` (+
  `EscalationRetryCount`) lets a split that's still invalid after exhausting its normal
  `RetryCount` against its primary model get a second, independent attempt budget against a
  *different* named model (must match a `ModelConfig.Name` under `models:` in `Config.yaml`).
  Validated at config-load time in `ConfigurationExtensions.GetConfiguration` (throws if the name
  doesn't match a configured model). Implementation lives entirely in `TranslateSplitAsync`
  (`TranslationService.cs`): the whole-cell + sentence-by-sentence retry loop was extracted into a
  local function `AttemptTranslationWithRetriesAsync(executingModel, maxRetries, isEscalation)` so
  the primary attempt (`modelConfig`, `RetryCount`) and the escalation attempt
  (`escalationModelConfig`, `EscalationRetryCount`) share identical logic instead of two copies
  drifting apart. Escalation only actually runs if `EscalationModelName` resolves to a model whose
  `Model` string differs from the primary one already tried (skips pointless re-attempts against
  an identical model) - **this makes it safe to point `escalationModelName` at the same model
  entry today as a placeholder**: it validates cleanly and is a documented no-op until a real
  second/stronger model is added under `models:` and the name is repointed, with no further code
  changes needed. `ValidationResult.EscalationAttempted` (set only when escalation actually ran)
  is surfaced in `UnprocessableItems.log` reasons (`[escalation attempted: yes/no]`) and a separate
  `_escalationAttemptCounter` (mirrors `_retryAttemptCounter`) is reported in the periodic progress
  log (`escalations this interval: N (total: M)`), so escalation's real cost/benefit is visible
  once a genuinely different model is configured - don't fold escalation attempts into the
  existing retry counters. `LlmHelpers.CalculateModelConfig` is unrelated to this and still a stub
  (`// TODO: Implement properly`, always returns `config.Runtime.Models.First().Value`) - that
  governs which model a split starts with, not escalation after failure.

## Testing conventions

- Prefer pure, fast unit tests against static utility methods (e.g. `CompoundFieldSplitter`) over
  running the file-based workflow tests, which mutate real working-directory state
  (`Files/Raw/Export`, `Files/Converted`, `Files/Mod`) and are meant to drive an actual translation
  run, not to be used as CI-style regression tests.
- When fixing a bug in fragment extraction/reconstruction, add a targeted xUnit test asserting the
  exact `Template`/`Fragments` shape rather than only checking round-trip equality — round-tripping
  alone won't catch "sentence split around an embedded number" style regressions.
