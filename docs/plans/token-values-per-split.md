# Token values per split (slotted storage)

Status: phases 1-5 built (2026-10-11), phase 6 (game rollout) not started. Supersedes the in-memory masking in `StringTokenReplacer` (`namedTokenReplacers`, built 2026-10); that masking is removed.

## Progress and corrections to the plan

- **Corrections found while verifying the plan against the code.** Merge lives in `GameFileHandlingBase.MergeFilesIntoTranslatedAsync`, not `TranslationCorpus` (which only loads files);
  the export copies translations onto the fresh export lines, so `tokens` is refreshed from the export by construction. `CompoundFieldSplitter.Decompose` keeps its signature
  (50+ call sites destructure it); export uses the new `DecomposeSlotted`. `ExportHelpers` only writes files; the decompose callers are `CsvGameDataWorkflow`, `JsonGameDataWorkflow`,
  `ExportHelpers.DecomposeFlatLine` (Prefab and DynamicStrings).
- **Template tokens (not in the plan).** About 4,160 of PoK's ~5,000 NPC tokens (3,936 in `NPC__SelfAction`, 116 in `NPC__PlayerAction`, 105 in `NPC__proAction`) are a lone code letter between colour
  tags (`<color=#E64D00>a</color>{0}`). They have no Chinese next to them, so the splitter leaves them in the template's literal text, never in a fragment: the in-memory approach never masked
  or restored them either, and they were packaged as a bare `a` (the game only substitutes `///a///` in English mode). `DecomposeSlotted` therefore slots the template too and the values are
  on `FieldTemplate.Tokens`; packaging restores the template before rebuilding the cell.
- **Slot names are unique within a cell, not just a split**, so QC and hooks can read the reconstructed cell: export numbers names across all fragments and the template, and each split keeps
  only the slots it uses.
- **Hooks.** Validators (`CustomColumnValidator`) and the pronoun checks get the restored forms; repair hooks (`CustomPostRepair`, `CustomColumnRepair`) stay in slot space, like the live
  translation path always was, because their output is stored.
- **Export loads the configuration when nothing has**, so an export-only run slots tokens (`ExportHelpers.EnsureTokensConfigured`).
- **Phase 4 golden comparison (PoK, 2026-10-11).** Baseline = the old code packaging a snapshot of the real `Files/Converted` (50 files) and 54 fixed rows, before any change.
  1. Old Converted (no slots, no `tokens`) packaged by the new code: all 51 files byte-identical to the baseline.
  2. The real Converted re-slotted in a temp copy (fragments only, 3,300 splits with tokens in the Touch and TaskProcess files) and packaged: all 51 files byte-identical.
  3. The same with template literals slotted too: only the six NPC files differ (3,860 lines), and every one is "a bare token in a template literal is now `///token///`" (whitespace aside:
     the old reconstruction put stray spaces round a bare `a`), 0 unexplained. These are the 3,936 + 116 + 105 template tokens that were packaged bare before. Old-style fragments
     (a sentence split around `gangName`) lose the spaces the bare word used to get, so they read `Let's///gangName///Here`: that data is re-exported with the placeholder patterns, not migrated.
  4. The fixed fixture (`Tests/Golden`, export, fake-translate, package) matches the baseline in 4 of 5 files; `NPC__SelfAction` differs in exactly its 12 template tokens.
  Run: `POK_GOLDEN_CORPUS=<snapshot of Files> dotnet test Tests --filter "FullyQualifiedName~PackagingGoldenTests"`; `POK_RESLOT_TEMPLATES=0` isolates fragments.
- **Not migrated.** Converted written before this change (raw tokens in `text`) matches nothing on merge; it must be re-exported and retranslated, as the plan's risks say.

## Why

Runtime tokens (names, gender alternatives, map or gang names, bare code letters) are hidden from the model behind descriptive slots
(`{npcName}`, `{playerTitle}`) and put back after translation. Today that exists only in memory: `Files/Converted` keeps the original token in
`text` and the restored game form in `translated`. Problems:

- `text` is unreadable (`致使a内息受阻`), and `translated` holds the game's syntax (`///a///`), not a readable slot.
- A slot restored from the glossary (`{playerTitle}`) cannot be reversed from the slot alone, because it stood for one of ~90 pairs. The
  restore therefore has to happen at translation time and can never be revisited.
- There is nowhere to translate a token's value. Some values must be copied (a name), some replaced by fixed text (`///a///`), some taken from the
  glossary (a title), and some are phrases that need translating once and reusing.
- Phrases that repeat or must read the same everywhere cannot be tokenised.

## Goal

Converted holds slots in `text` and `translated`, and a per-split `tokens` map holds each slot's real value. Packaging is the one place values are
restored. A token's value can be copied, replaced by fixed text, looked up in the glossary, or translated once and cached.

```yaml
- split: 5
  text: "致使{npcName}内息受阻，全身乏力。"
  translated: "{npcName} has blocked qi."
  tokens:
    npcName: { source: "a" }
- split: 5
  text: "嗯……{playerFirstName}{playerTitle}若要小玥同行，便来临川村寻我。"
  translated: "Hm... {playerFirstName} {playerTitle}, find me if you want Xiaoyue to join you."
  tokens:
    playerFirstName: { source: "fp" }
    playerTitle: { source: "[大哥||姐姐]" }
```

`source` is the original text. `translated` appears in a token only when the kind translates its value (phase 5). Names stay unique within a split
(`playerTitle`, `playerTitle2`); the same name with the same source is reused. `tokens` is written only when non-empty, so token-free data is
unchanged.

## Token kinds (config)

Extends `NamedTokenReplacer` (`literal`/`pattern`, `name`, `description`). The existing `restoreAs` and `restoreFromGlossary` stay as aliases.

| Kind | Restored value | Resolved | Example |
| --- | --- | --- | --- |
| `copy` (default) | the source text | packaging | `<fName>` |
| `fixed` (`restoreAs`) | a configured text | packaging | `a` -> `///a///` |
| `glossary` (`restoreFromGlossary`) | the glossary Result for the source | packaging, so a glossary fix needs no retranslation | `[大哥||姐姐]` -> `<Big Brother||Big Sister>` |
| `translate` (phase 5) | the source translated once (glossary first, then the model), cached | translation time, stored in `tokens.<name>.translated` | a fixed phrase, a title with no glossary entry |

Optional `showValue: true` adds the value (translated, for `translate`) to the prompt line for that slot, so the model has the meaning without
being able to rewrite it.

## Phrase tokens

A `pattern` may match a phrase, not just a name. Good candidates: a boilerplate phrase repeated across many lines, a runtime-inserted phrase the
game builds, text that must read identically everywhere, Chinese embedded inside tags. Poor candidates: ordinary prose (the model loses the grammar
around the slot; prefer a glossary entry). Watch a/an, plurals and capitalisation around a slot, and use `showValue` so the model sees what it stands
for. A repeated phrase translated once is also a consistency and cost win.

## Pipeline changes

| Stage | Change | Where (verify before editing) |
| --- | --- | --- |
| Model | `TranslationSplit.Tokens` (`Dictionary<string, TokenValue>`), serialised only when non-empty | `Support/TranslationSplit.cs` |
| Slotting | A pure, stateless `TokenSlotter`: `Slot(text) -> (slotted, tokens)` and `Restore(slotted, tokens)`. `StringTokenReplacer` keeps its numbered `{n}` masking, skips configured slot names, and no longer touches named tokens. The `namedMap` state and `MaskNamedInTranslation` are gone (the private-use markers survive only inside one `Slot` call). **Built.** | `Utility/StringTokenReplacer.cs`, new `Utility/TokenSlotter.cs`, `Support/TokenValue.cs` |
| Export | New `DecomposeSlotted` returns slotted fragments plus a token map per fragment and for the template (`Decompose` keeps its signature); export workflows write `tokens` (splits) and `tokens` (templates). **Built.** | `Utility/CompoundFieldSplitter.cs` (`DecomposeSlotted`), `Utility/ExportHelpers.cs`, `Workflow/CsvGameDataWorkflow.cs`, `JsonGameDataWorkflow.cs`, `PrefabTextWorkflow.cs`, `DynamicStringWorkflow.cs` |
| Merge | Matches on slotted `text`; `tokens` is refreshed from the export every time (the export is the source of truth), never kept from the old data. **No code change needed**: the merge already writes the export lines with translations copied onto them. | `GameFileHandlingBase.MergeFilesIntoTranslatedAsync` |
| Prompt | Slot descriptions as now; `showValue` lines; no restore | `TranslationService.GenerateBaseMessages` |
| Validation | In slot space: every slot in `text` appears at least as often in the candidate, no invented slot, no unknown `{word}` (`TokenSlotter.FindSlotProblem`). Validators (`CustomColumnValidator`) and the structural check run on the **restored** form so game logic is unchanged; repair hooks stay in slot space because their output is stored. **Built.** | `Workflow/TranslationWorkflow.cs` (`EvaluateRules`, `TryApplyGameSpecificRepair`), `LineValidation.cs` (unchanged), `Workflow/PronounDefectWorkflow.cs` (restored forms) |
| QC | Operates on slotted text on both sides, with the whole column's tokens (fragments and template) for the rule check; `MaskNamedInTranslation` is deleted. **Built.** | `Workflow/QualityControlWorkflow.cs`, `QualityControlAssessmentWorkflow.cs` |
| Packaging | Restore from `tokens` (fragments, template, whole-cell `qcTranslated`) after the translation is chosen and before the cell is rebuilt; unresolvable value fails the line to raw fallback with a reason. **Built.** | `Utility/PackagingHelpers.cs` (`RestoreSlots`), and the Csv, Json, Prefab and DynamicString workflows through it |
| Manual translations | Keyed by the text with its slots replaced by their sources; a manual result containing a token's final text is re-slotted; one that leaves a slot of the source out is skipped and logged (flagging it for retranslation would re-flag it on every pass). **Built.** | `TranslationWorkflow.TryApplyManualTranslation` |
| Diagnostics, gold set | Diagnostics show slotted and restored forms and each slot's description; the QC assessment slots gold-set strings (`MaskSample`) and re-slots their translations. **Built.** | `Utility/TranslationDiagnostics.cs`, `QualityControlAssessmentWorkflow.cs` |

## Skills to update

The skills in `.claude/skills/` describe the pipeline by stage, so they must change with it. LlmKit's copy is canonical; every game repo holds a
verbatim copy (`new-translation-project` copies them), so update LlmKit first and then sync `g:\PathOfKungfuOverLlm\.claude\skills\` (finish with
`diff -rq` between the two folders). Follow each skill's existing format and length; do not restate what the docs already say, link to them.

| Skill | Change |
| --- | --- |
| `find-runtime-tokens` | Rewrite step 4 (classify) around the four kinds (`copy`, `fixed`, `glossary`, `translate`) and phrase tokens, including when not to tokenise prose; step 5 and 7 now describe slotted `text`/`translated` and the `tokens` map; add the checks "no slot left in packaged output" and "tokens refreshed on re-export". Keep the decompile, scan and in-game steps. |
| `new-translation-project` | Add a step that, once the first dump exists, runs `find-runtime-tokens` and wires `namedTokenReplacers` into the starter `Config.yaml`; note that Converted holds slots when tokens are configured. |
| `investigate-packaging-issue` | Add the token-restore stage to the fixed investigation order (unresolved slot, missing `tokens` entry, glossary kind with no entry, hook run on restored vs slotted text) and the symptom "a `{slot}` or `///token///` is wrong in `Files/Mod`". |
| `investigate-missing-translation` | Add the same stage to the stage-by-stage walk (extraction, Converted, QC and packaging fallback, injection): a line can lose its value between `tokens` and the packaged cell. |
| `diagnose-translation-failure` | The report shows slotted and restored forms and each slot's description; say how to read a failure that is only a slot problem (invented, dropped or renamed slot) versus a model problem. |
| `investigate-qc-issue` | QC runs on slotted text on both sides; a QC defect about a name or title is now a slot question. Remove references to masking the translation separately. |
| `add-glossary-fix` | A `glossary`-kind token value is a normal glossary line keyed by the token's source text and is resolved at packaging, so fixing it needs no retranslation; say where it lives (game glossary, never the preset for game syntax). |
| `expand-goldset` | Gold-set cases from lines with tokens store slotted text and the `tokens` map (or the string-level slot/restore), so a case stays valid after a glossary change. |
| `add-character-gender-context` | Mention that gender-unknown person slots (the player's name and title) are described as such in the token config; unknown-gender tokens in `GameHooks.UnknownGenderPersonTokens` are the restored forms. |
| `compare-translation-models`, `build-game-glossary` | Read for token wording; update only if they describe masking or Converted token forms. |

Each skill change lands in the phase that changes its subject (packaging skills in phase 4, `find-runtime-tokens` in phases 5 and 6), not at the end.

## Phases

Each phase ends green (`dotnet test` for LlmKit; in a game repo only filtered tests, never the unfiltered project) and is a separate commit-sized step.

1. **Baseline (done: PoK `Tests/PackagingGoldenTests.cs`, `Tests/Golden/`, a full-corpus snapshot comparison).** In a game with tokens, snapshot the packaged output for a fixed set of rows from the current Converted as golden files. Record LlmKit's
   test state (one known failure: `Write throws after exhausting retries against a lock that never clears`).
2. **Slotter (done).** `TokenSlotter` with unit tests: names, suffixes, glossary and fixed restore, digits in names, slots next to numbered slots, missing and
   invented slots, round trip. `StringTokenReplacer` delegates; existing `NamedTokenReplacerTests` pass unchanged.
3. **Schema and export (done, template tokens added).** `Tokens` on the split, `Decompose` slots fragments, merge refreshes tokens. Test: export a sample, re-export is idempotent, old
   data without `tokens` still loads.
4. **Translate, validate, QC, packaging in slot space (done; golden comparison under "Progress and corrections").** Remove the markers and `MaskNamedInTranslation`. Gate: package from re-slotted sample data and
   compare with the golden output; token-free files must be byte-identical.
5. **Value kinds (done).** `translate` with a cache and `showValue`; phrase-token tests (a repeated phrase is translated once; a missing glossary entry falls
   back to the model; the cache key includes the glossary).
6. **Game rollout (PathOfKungfuOverLlm).** Switch on, re-export, retranslate flagged and stale token lines in small batches, adapt
   `GameFileHandling.ValidateRuntimeTokens` and `PlayerTokenPrompt.txt` if needed, update `docs/investigations/runtime-tokens.md`, update the
   `find-runtime-tokens` skill (storage is now slotted, kinds are config), sync the skill copies into the game repo and confirm with `diff -rq`, then an
   in-game check in English mode.

## Phase 5 decisions

- Translated token values are stored **in the split** (`tokens.<name>.translated`), the plan's simpler, diffable option; merge keeps one for an unchanged source. A separate cache file was not needed.
- The glossary kind (and a `translate` token's glossary lookup) resolves **at packaging only**; no snapshot is taken at translation time.
- A `translate` token slots only text that contains Chinese, so a stray ASCII match is left alone. A phrase is translated on its own, so a token inside it is not slotted.
- `showValue` makes a translation depend on the shown values: such a split bypasses the shared translation cache and dedups only against splits showing the same values.
- Per-file scoping of a token (`files:`) is still open; a strict pattern does it today.

## Risks

- A packaging bug puts wrong tokens into the game: hence golden files before any change and the phase 4 gate.
- Old Converted data cannot be migrated in place (its fragments were split without placeholder patterns); it must be re-exported and retranslated.
- Hooks that expect restored text: run them on the restored form, test with the PoK validator.
- Slots changing equality: two lines differing only in a token source now share `text`; confirm cache keys and merge matching are what we want (they dedupe, which is intended).
- Model behaviour around slots (articles, plurals): measure with a before/after sample, do not assume.

## Open questions

- Store `translated` token values in the split, or in a separate per-file value cache keyed by source? (Split is simpler and diffable; a cache dedupes across files.)
- Should the glossary kind resolve at packaging only, or also snapshot at translation time for the gold set?
- Per-file scoping of a token (letters only in NPC files) is currently done by a strict pattern; is a `files:` filter worth adding?

## Definition of done

LlmKit tests pass (bar the known failure); packaged output for a token-free game is byte-identical; PoK packaged token lines match the intended forms in the
golden comparison; a repeated phrase token is translated once; docs (`ARCHITECTURE.md`, a feature page) and every skill in the table above describe the
final design, and the game repo's skill copies match LlmKit's.
