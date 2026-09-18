# Downstream translation-project test organization

Part of the [downstream translation-project structure](downstream-project-structure.md). Source of truth:
`DragonHierOverLlm/Tests/*.cs` as of 2026-09-16 (17 `.cs` files, skimmed for class names, `[Fact]`/
`[Theory]` `DisplayName`s, and one-line purpose — bodies not fully digested except where the
numbering scheme needed the surrounding comment to make sense).

## One file per workflow vs. shared files

Some workflows get a **dedicated test file whose class name matches the workflow it drives**;
others share a file because their facts are steps of one linear pipeline stage. Observed split in
DragonHeir:

| File | Class | What it covers |
| --- | --- | --- |
| `QualityControlWorkflowTests.cs` | `QualityControlWorkflowTests` | Every QC-pass entry point: sample run, full run, apply-rules, triage, fix-prompt generation, and the numbered reset facts (see below). One dedicated file per major workflow (here, `QualityReviewWorkflow`) is the pattern to follow when a workflow has enough entry points/reset facts to justify its own file. |
| `TranslationWorkflowTests.cs` | `TranslationWorkflowTests` | The main translate → apply-rules → translate-lines → find-failures → flag/clean-up-regex pipeline. Multiple loosely related `[Fact]`s share one file because they're sequential steps of the *same* linear workflow, not because the workflow is small. |
| `FileInputWorkflowTests.cs` | `FileInputWorkflowTests` | Extraction-side steps: export assets/prefab-text into translated, IL2CPP string-map/column/name/poetry/etc. candidate extraction, dedupe, merge, line-count check. |
| `FileOutputWorkflowTests.cs` | `FileOutputWorkflowTests` | Packaging-side steps: package to game files, zip release. |
| `AssetDumperWorkflowTests.cs` | `AssetDumperWorkflowTests` | Copy raws to working directory, dump Chinese text from prefab/asset files. |
| `FileValidationTests.cs` | `FileValidationTests` | Post-packaging structural invariants (column-count consistency, row-count parity with raw dumps, CSV round-trip integrity, no unresolved `{n}` placeholders, id/lookup-looking cell detection). Unnumbered — these are standing assertions, not pipeline steps. |
| `GlossaryCreationTests.cs` | `GlossaryCreationTests` | Glossary generation. |
| `TextResizerTests.cs` | `TextResizerTests` | Text-resizing logic, unrelated to the translation pipeline steps above. |
| `GameFileHandling.cs`, `TranslationExport.cs`, `TranslationPackaging.cs`, `TextFileConfiguration.cs`, `DynamicStringSources.cs`, `DynamicStringExtraction.cs`, `DrinkQuoteWorkflow.cs`, `PoetryDataWorkflow.cs` | `public static class` (no `[Fact]`s) | Support/config classes referenced by the `[Fact]` methods above (working-directory paths and hooks, `TextFilesToSplit` config, per-game-specific extraction helpers). Not test files in the xunit sense — they're the fixture/config layer the dedicated-workflow test files call into. |

**Rule of thumb:** give a workflow its own file (named `<Workflow>Tests.cs`, class
`<Workflow>Tests`) once it has enough distinct entry points and reset/regression facts to need its
own numbered sequence (QC is the clearest example — 10 QC-specific reset/entry facts would clutter
`TranslationWorkflowTests.cs`). Keep steps of one linear pipeline stage (translate, extract,
package) together in one file per stage rather than one file per method.

## Numbering / `DisplayName` convention

Within a dedicated workflow test file, `[Fact(DisplayName = "N. Verb Phrase")]` numbers facts in
the order a human would actually run them for a normal pass, **not** in file order or
alphabetical order:

- **`"0. ..."` is reserved for a brute-force/reset/combined entry point** — something that resets
  or re-derives state from scratch rather than doing an incremental step. Two real examples:
  - `TranslationWorkflowTests`: `"0. Reset All Flags"`.
  - `QualityControlWorkflowTests`: `"0. TranslateAndQualityReviewBruteForce"` — a **combined**
    brute-force fact that runs `TranslationWorkflow.TranslateLinesBruteForce`, then
    `QualityReviewWorkflow.RunBruteForce`, then packages, in one call. This exists specifically so
    a "glossary changed / bad word added / game needs a repair" reset touches *both* the
    translation and QC brute-force paths and re-packages, instead of a human having to remember to
    run two separate numbered facts (`"1"` in `TranslationWorkflowTests` and a QC brute-force) and
    possibly forgetting one. **This is the fact that was missing from `WanXiangOverLlm`'s QC test
    file before this doc was written** — a future skill/reconciliation pass should specifically
    check for a `"0. ..."` combined-brute-force fact in any QC test file and flag its absence.
  - `AssetDumperWorkflowTests`: `"0. Copy raws to Working Directory"` (and a `"0b."` variant for a
    second raw-copy substep — `0`-prefixed facts may branch into `0a`/`0b` etc. for closely related
    substeps of the same reset step, not just a bare `"0."`).
- **`1` through `9` (or higher, e.g. `"99."`, `"999."`) number the normal, linear run order for
  that file's workflow** — run `1`, then `2`, then `3`, etc. in a fresh pass. Gaps and
  re-used numbers happen legitimately when a fact is scoped to one purpose:
  `QualityControlWorkflowTests` has **three separate facts sharing the display number `"7."`**
  (`"7. Reset Qc Retry Limits"`, `"7. Reset Low-Score Quality Review State"` appears alongside a
  `"8."` and `"9."` too) — this is tolerated because they're alternative/branching reset paths for
  different situations at the same rough point in the sequence, not because the numbering is
  sloppy. Don't "fix" duplicate numbers by renumbering unless you're also verifying no doc/runbook
  cross-references the old number.
- **Facts with no number in the `DisplayName` are one-off reset/regression facts**, not part of
  the normal sequential run order — e.g. `"Reset Leaked Quality Review Corrections"`,
  `"RunQualityReviewSampleForOneLine"`, `"QcOmittedSubjectRegression"`, `"Reset ALL Quality Review
  State (full re-review)"`. These are named as full descriptive phrases (not "N. Verb Phrase") and
  typically exist to reproduce/guard against one specific historical bug or to let a developer
  target one row/file without perturbing the rest of the corpus's QC state.
- Sub-lettered variants (`"4a."`, `"4b."`, `"4b2."`, `"4b3."`, `"4c"`, `"4d."`, `"4e."`, `"4f."`,
  `"4g."`) appear in `FileInputWorkflowTests` where several independent, game-specific extraction
  passes all logically sit at "step 4" (candidate extraction) but don't have a natural linear
  order among themselves.

## The DragonHeir vs. WanXiang QC drift this section fixed

`WanXiangOverLlm`'s `QualityControlWorkflowTests.cs` had drifted onto a **`"3a"/"3b"/"3c"`
sub-lettered scheme** for what DragonHeir numbers `"0"`–`"9"` as flat top-level steps, and was
missing the `"0. TranslateAndQualityReviewBruteForce"` combined fact entirely (WanXiang only had
the equivalent of DragonHeir's `"1"`/translation-only brute-force, with no QC-inclusive combined
reset). A reconciliation pass (manual, or a future `upgrade-translation-project` skill run) against
a QC test file should check for exactly these two things:

1. Does the QC test file use flat `"0"`–`"9"` numbering (branching to `"0a"/"0b"` etc. only for
   substeps of one reset step), rather than inventing its own lettering scheme?
2. Does it have a `"0."`-numbered fact that brute-forces **both** translation and QC state and
   re-packages, not just a QC-only brute-force?

## Project boundary

The required downstream layout keeps reusable workflow/configuration code in `Translate/` and
numbered pipeline facts plus regression tests in `Tests/`. The file/class/numbering conventions
above apply to `Tests/`; `Translate/` should not become a second test-runner or a place for numbered
operational facts.
