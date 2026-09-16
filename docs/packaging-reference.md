# Packaging — architecture reference

> Current-state reference for how translated `Line → Splits → (Templates)` data gets reassembled
> back into the shape a downstream game actually consumes — describes **what the code does
> today**, not the design process. For the QC pass that packaging shares a score-gate/freshness
> mechanism with, see [`quality-review-pass-architecture.md`](quality-review-pass-architecture.md)
> (this file links to it rather than re-explaining QC internals). For the real incident that
> shaped the current raw-fallback rules, see
> [`../../DragonHierOverLlm/Tests/docs/qc-run-startup-crash-investigation-2026-09-15.md`](../../DragonHierOverLlm/Tests/docs/qc-run-startup-crash-investigation-2026-09-15.md).

## What it is

Packaging is the final step of the pipeline: read every file's accumulated `Files/Converted/*.yaml`
(`TranslationLine`/`TranslationSplit`/`FieldTemplate` records built up by translation and,
optionally, QC), reconstruct each translatable cell/line back into the exact shape the game
expects, and write the result to `Files/Mod/*`. Four workflow classes do this, one per
`TextFileType`, all in this repo:

- `Workflow/CsvGameDataWorkflow.PackageAsync` — `TextFileType.RawCsv`.
- `Workflow/JsonGameDataWorkflow.PackageAsync` — `TextFileType.RawJson`.
- `Workflow/PrefabTextWorkflow.PackagePrefabTextAsync` — `TextFileType.PrefabText`.
- `Workflow/DynamicStringWorkflow.PackageDynamicStringsAsync` — `TextFileType.DynamicStringsIL2CPP`.

Every one of them returns `(int Passed, int QcRejected, int RawFallback)` — the same three-bucket
shape (`Support/PackagingFailureReason.cs`'s `None`/`QcRejected`/`RawFallback`) regardless of file
type, so a consuming project's packaging test can report a uniform summary across every file
without knowing which workflow produced which number.

A consuming project's own packaging entry point (e.g. `TranslationPackaging.PackageFinalTranslationAsync`
in a downstream repo) dispatches each configured `TextFileToSplit` to the matching workflow by
`TextFileType`, then does any project-specific post-processing (see "Downstream wiring" below).
None of this reassembly touches `Files/Converted` — packaging only decides what gets written to
`Files/Mod`; `Translated`/`QcTranslated`/`QcQualityScore` are read, never modified.

## Reconstruction: templated vs. plain

Every workflow follows the same two-shape logic per translatable unit (a CSV column, a JSON field,
a whole PrefabText/DynamicString line):

1. **Templated** (a `FieldTemplate` exists for that column/field) — fetch every fragment
   (`line.Splits.Where(s => s.Split == column)`, ordered by `SubIndex`), and either use the QC
   anchor's `QcTranslated` wholesale (see "Packaging's use of the QC score-gate" below) or
   reconstruct via `CompoundFieldSplitter.Reconstruct(template.Template, fragments.Select(f =>
   f.Translated))` — the inverse of the `Decompose` call export-time used to build the template.
2. **Plain** (no template — a trivial single-fragment column) — just that split's `Translated` (or
   `QcTranslated`, same gating), no reconstruction needed.

If any fragment in a templated column is `FlaggedForRetranslation`, `!SafeToTranslate`, or has
non-empty source `Text` but no `Translated` yet, the whole column/line is treated as not-ready and
falls through to that workflow's raw-fallback behavior (see below) — a partially-translated
reconstruction is never written.

## Packaging (score-gating + freshness)

Every packaging path applies the same two checks per column, both gated on
`Utility.QualityReviewHelpers.IsQcReviewFresh` — see
[`quality-review-pass-architecture.md`](quality-review-pass-architecture.md#packaging-score-gating--freshness)
for the full mechanism (this section only covers what packaging itself does with the result):

1. If fresh and `QualityReviewHelpers.PassesQcScoreGate(QcQualityScore, QcDefectCategory,
   qualityReview)` returns `false` → the proposed `QcTranslated` correction is discarded, but
   that's **all** that's discarded — the column still packages normally on its ordinary pre-QC
   `Translated` text, exactly as if QC had never touched it. This is reported as
   `PackagingFailureReason.QcRejected`, a distinct bucket from `RawFallback`, precisely so a
   caller can tell "QC's correction was held back but the line still packaged fine" apart from "the
   line packaged nothing at all."
2. Else if fresh and `QcTranslated` is non-empty → use it in place of `Translated` (for a
   templated column, this bypasses `CompoundFieldSplitter.Reconstruct()` for that column entirely,
   using the anchor's `QcTranslated` as the literal cell/line value).
3. Otherwise (never reviewed, reviewed-but-stale, or `qualityReview.enabled: false`) → falls
   through to ordinary `Translated`-based packaging, unaffected by anything QC-related.

`qualityReview.enabled: false` gates packaging too, not just the QC pass — flipping it off and
re-running packaging (no LLM calls) makes every column package as if QC had never touched it, with
`minAcceptableScore`/`autoAcceptDefectCategories` never consulted even for columns that already
have a stored `QcTranslated`/`QcQualityScore`. This is the standard way to isolate "did QC's
correction introduce this defect" from "was it already there before QC touched it" — see the
startup-crash investigation linked at the top of this file for a worked example of using this to
isolate a real production bug.

**CSV vs. JSON never fail purely for a low QC score.** `CsvGameDataWorkflow.PackageAsync` and
`JsonGameDataWorkflow.PackageAsync` only ever use the score gate to decide whether to skip the
`QcTranslated` shortcut — every genuine packaging failure they report is a `RawFallback`, never a
`QcRejected`, because the row/field always has an ordinary pre-QC `Translated` fallback to use
instead. `PrefabTextWorkflow`/`DynamicStringWorkflow` are the two paths that can report a real
`QcRejected` count (see "Raw-fallback behavior" below for why the distinction matters there).

## Differences in packaging behavior by workflow

The four workflows share the score-gate/freshness logic above, but differ in unit of reconstruction,
failure granularity, and what happens when nothing is packageable:

| | `CsvGameDataWorkflow` | `JsonGameDataWorkflow` | `PrefabTextWorkflow` | `DynamicStringWorkflow` |
| --- | --- | --- | --- | --- |
| Reconstruction unit | one CSV row (`Mod/{file}`, plain text) | one JSON object (`Mod/{file}`, JSON array) | one line (`Mod/{file}.yaml`, `raw`/`result` list) | one line (`Mod/{file}.yaml`, `raw`/`result` list) |
| Failure granularity | whole row falls back together | scoped to the single failing field — every other field in the same object still packages | whole line falls back | whole line falls back |
| Lookup semantics at runtime | positional (CSV columns) | property path | exact whole-string match | substring replacement inside a larger string |
| On a genuine failure (`RawFallback`) | writes the row's original raw CSV line untouched | leaves just that field at its original raw value; object still emits | **omits the line's dictionary entry entirely** | **omits the line's dictionary entry entirely** |
| `SkipColumns` | preserved byte-for-byte from the parsed row, including a stale compound template, never touched by translation state | n/a (`"...Tw"`/`"...Final"` sibling properties skipped instead — a JSON-specific convention) | n/a (flat one-column-per-line file) | n/a (flat one-column-per-line file) |
| Extra packaged artifact | none | none | none | a single-fragment template also emits a deduplicated **bare-label** dictionary entry (see below) |

`CsvGameDataWorkflow` is also the one workflow whose column loop distinguishes templated columns
from plain columns as two separate passes over the same row (`line.Templates` first, then
`line.Splits` for the columns not already covered by a template) — both passes independently
respect `SkipColumns` and both can independently set `failed = true` for the same row, which is why
an earlier hand-rolled predecessor of this logic (see the CSV workflow's own doc comment) had two
different places a "skip this column" check could be missed.

### DynamicString-specific: the bare-label entry

A single-fragment templated line (e.g. `"打扰了;GovernPlotStart;1"`, decomposed into label `"打扰了"`
+ a literal `";GovernPlotStart;1"` template tail) represents a game-data cell where the trailing
literal is action/parameter metadata the game strips off before ever rendering the label — an NPC
dialogue-option button shows only the label, never the full compound string. Because the full
reconstructed entry can only ever match a runtime string that bakes the *entire* literal in
verbatim, `DynamicStringWorkflow.PackageDynamicStringsAsync` also emits the bare label as its own
deduplicated `DynamicStringResult` entry (`seenBareRaw` guards against emitting the same bare pair
twice across lines), so the runtime substring match fires whichever shape the game actually used.

**Known limitation**: when a templated column's whole-cell `QcTranslated` correction is used
instead of `Reconstruct()`ing from fragments, there's no way to derive the bare-label pair from a
single freehand-rewritten sentence (the same ambiguous reverse-mapping problem the `SubIndex == 0`
anchor convention exists to avoid elsewhere) — so a QC-corrected multi-part dynamic-string line
loses its bare-label entry. The full reconstructed entry still packages correctly regardless; only
the dialogue-button-specific shortcut entry is missing for that one line.

## Raw-fallback behavior when a line fails QC or is stale

Independent of QC, a column/line can be genuinely unusable at packaging time: unsafe
(`!SafeToTranslate`), flagged for retranslation, missing its translation entirely, or the file has
`PackageOutput = false`. This is `PackagingFailureReason.RawFallback`, and the four workflows
diverge sharply here:

- **`CsvGameDataWorkflow`/`JsonGameDataWorkflow`** package these with their original raw
  value — a CSV row structurally must have *something* in every column, and a JSON object commonly
  has other fields that packaged fine, so writing the untranslated raw text into just the failing
  slot is the only option that doesn't corrupt the row/object shape.
- **`PrefabTextWorkflow`/`DynamicStringWorkflow`** package a flat raw-string dictionary instead,
  where every entry is optional. As of 2026-09-16 a `RawFallback` line here is **omitted from the
  packaged dictionary entirely** rather than written as an explicit raw-Chinese entry.
  `ReconstructLine` returns `(null, PackagingFailureReason.RawFallback, ...)` for this case; the
  caller still counts it toward the reported `RawFallback` total even though nothing is added to
  the output list.

This matters most for `DynamicStringWorkflow`, consumed as a runtime *substring* replacement: an
explicit raw-Chinese dictionary entry risks the runtime's own "does this text still contain
untranslated Chinese" check matching the very entry that was deliberately left as Chinese,
re-triggering whatever that check does on every match — a replace-and-recheck cycle that can loop.
Omitting the entry instead means no substitution happens at all: visually identical to a
raw-Chinese entry (the original text is untouched either way), but with no re-match risk. For
`PrefabTextWorkflow`, whose dictionary is looked up by exact whole-string match, an omitted entry
simply means no replacement fires and the UI keeps showing whatever it already had — again visually
equivalent to a raw-fallback entry, without ever writing an explicit Chinese "translation."

### This was a real bug until 2026-09-16

`PrefabTextWorkflow`/`DynamicStringWorkflow` used to treat a score-gate failure (`QcRejected`) the
same as a genuine `RawFallback` — discarding the column all the way down to raw, untranslated
Chinese text (`line.Raw`/`split.Text`) instead of just discarding the rejected `QcTranslated`
correction and keeping the perfectly good pre-QC `Translated` text.
`CsvGameDataWorkflow`/`JsonGameDataWorkflow` never had this bug; they always fell back to
`Translated` correctly, since a `QcRejected` there was never anything more than "skip the
`QcTranslated` shortcut." The bug shipped raw Chinese UI text — including an age-rating splash
notice shown on the very first boot screen — into a real build and broke game startup. See
[`../../DragonHierOverLlm/Tests/docs/qc-run-startup-crash-investigation-2026-09-15.md`](../../DragonHierOverLlm/Tests/docs/qc-run-startup-crash-investigation-2026-09-15.md)
for the full diagnosis. Fixed by scoping the score-gate check to only skip the `QcTranslated`
shortcut, never the ordinary fragment/`Translated` reconstruction — the same distinction the
"Packaging" section above describes as steps 1 vs. 3.

## Packaging-time text fixups (`Utility/PackagingTextFixups.cs`)

Every workflow above runs each cell/line's packaged text through `PackagingTextFixups.Apply(config,
textFile, column, raw, result)` right before it's written — the single call site for deterministic,
packaging-time text repairs, so a fixup only needs to be added once rather than duplicated across
`CsvGameDataWorkflow`, `JsonGameDataWorkflow`, `PrefabTextWorkflow`, and `DynamicStringWorkflow`.
`column` is the zero-based CSV column index when known, `null` for a plain PrefabText/DynamicString/
JSON-field entry with no column context.

`Apply` runs two standard, game-agnostic fixups in order:

1. **Hyphen undo** — the LLM/QC pass occasionally "typographically improves" an ASCII hyphen-minus
   into the Unicode look-alike non-breaking hyphen U+2011. Undone unconditionally; a genuine U+2011
   in source text isn't a realistic scenario for translated prose.
2. **Literal-newline undo** — the LLM/QC pass occasionally emits a literal backslash-n (`\n` as the
   two characters `\` and `n`) in a translated result instead of preserving the raw text's own
   newline convention. Only fixed up when `raw` itself never contains a literal backslash-n — so
   raw text that genuinely encodes its line breaks as literal `\n` (e.g. some prefab text/CSV cells
   where the game's own display code replaces `\n` with a real line break at render time, rather
   than the raw text using an actual newline character) is left untouched, since that's this field's
   own convention and any `\n` the result produces is presumably intentional too. Note this compares
   literal backslash-n against a literal backslash-n, not against a real newline character — `raw`
   commonly contains real newline characters of its own that are irrelevant to this check.

After the standard fixups, `Apply` invokes `config.Hooks.CustomPackagingFixup` (`Func<TextFileToSplit?,
int?, string, string, string>?`, on `GameHooks` — see
[`quality-review-pass-architecture.md`](quality-review-pass-architecture.md) for the sibling
QC-side hooks on the same `GameHooks` instance) if the consuming project has registered one, passing
its return value through as the final result. This is the extension point for a game-specific
packaging-time repair — register it once on `LlmConfig.Hooks` and it runs everywhere packaging
happens, without needing its own call site in any workflow. Left `null` (no-op) unless a caller
opts in.

## `PackageOutput` and `SkipColumns`

- `TextFileToSplit.PackageOutput` (bool, default `true`) — a per-file kill switch: when `false`,
  every column/line in that file is treated as unpackageable (`RawFallback` for CSV/JSON, omitted
  entirely for PrefabText/DynamicString), regardless of translation state. Used to hold a file back
  from shipping without removing it from the translation/QC pipeline.
- `TextFileToSplit.SkipColumns` (CSV/JSON only) — a per-file, per-column opt-out from decomposition
  at *export* time (icon/resource-path columns, structural lookup keys). A skipped column is never
  translated in the first place, so packaging leaves it byte-for-byte from the raw parsed row —
  `CsvGameDataWorkflow.PackageAsync` explicitly checks `SkipColumns` in both its templated-column
  and plain-column passes so a stale leftover template/split for a since-skipped column can never
  overwrite the cell with a partial/mangled value.

## Wiring this into a new project

Each workflow's `PackageAsync`/`PackagePrefabTextAsync`/`PackageDynamicStringsAsync` takes the same
`(workingDirectory, textFile)` shape (`CsvGameDataWorkflow.PackageAsync` also accepts two optional
hooks — see below) and returns the same `(Passed, QcRejected, RawFallback)` tuple, so a downstream
project's own packaging entry point is a thin dispatch loop over its configured
`TextFileToSplit[]`, grouped by `TextFileType`:

```csharp
public static async Task PackageFinalTranslationAsync(string workingDirectory, TextFileToSplit[] textFiles)
{
    foreach (var textFile in textFiles.Where(f => f.Type == TextFileType.PrefabText))
        await PrefabTextWorkflow.PackagePrefabTextAsync(workingDirectory, textFile);

    foreach (var textFile in textFiles.Where(f => f.Type == TextFileType.DynamicStringsIL2CPP))
        await DynamicStringWorkflow.PackageDynamicStringsAsync(workingDirectory, textFile);

    foreach (var textFile in textFiles.Where(f => f.Type == TextFileType.RawJson))
        await JsonGameDataWorkflow.PackageAsync(workingDirectory, textFile);

    foreach (var textFile in textFiles.Where(f => f.Type == TextFileType.RawCsv))
        await CsvGameDataWorkflow.PackageAsync(workingDirectory, textFile);
}
```

`CsvGameDataWorkflow.PackageAsync` additionally accepts:

- `onColumnPackaged` (`Action<int, string, string>?`) — a per-column callback (column index, raw
  text, packaged text) for a project that needs to observe what was written per column without
  this library knowing why (e.g. building a side-channel lookup file from specific columns).
- `rowPostProcess` (`Func<string[], string[]>?`) — a final fixup over the whole row's fields just
  before `CompoundFieldSplitter.RebuildCsvRow` — e.g. a workaround for one game's own naive CSV
  loader misreading a trailing comma before a closing quote. Neither hook runs for a row that fell
  back to its original raw text.

Packaging is safe to rerun at any time straight from `Files/Converted` — it never mutates
`Converted`, only overwrites `Files/Mod`. This makes it the cheapest way to test the effect of a
config change (`qualityReview.enabled`, `qualityReview.minAcceptableScore`,
`autoAcceptDefectCategories`, a new `SkipColumns` entry) without any LLM calls: change the config,
rerun packaging, diff `Files/Mod`.

Report the three-bucket `(Passed, QcRejected, RawFallback)` count per file from whatever test/task
wraps these calls (see a downstream repo's own packaging test fact) — `QcRejected` and
`RawFallback` are deliberately separate buckets precisely so a regression in either shows up as a
distinct, attributable number rather than a single opaque "didn't package" count.
