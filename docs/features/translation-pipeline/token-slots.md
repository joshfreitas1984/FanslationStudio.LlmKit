# Token slots (`namedTokenReplacers`)

A game substitutes runtime tokens into its text: a player or NPC name, a gender alternative, a map or gang name, a bare code letter.
They must reach the model under a name that says what they are and come back in the game's own format. With `namedTokenReplacers`
configured, **Converted holds the name (a slot) and the real value lives next to it**, and the value is put back in one place: packaging.

Plan and rationale: [`../../plans/token-values-per-split.md`](../../plans/token-values-per-split.md). Finding the tokens of a new game:
`.claude/skills/find-runtime-tokens/SKILL.md`. A worked game: `docs/investigations/runtime-tokens.md` in PathOfKungfuOverLlm.

## Shape of the data

```yaml
- raw: "5,0,致使a内息受阻，全身乏力。"
  splits:
  - split: 17
    text: "致使{npcName}内息受阻，全身乏力。"
    translated: "{npcName} has blocked qi."
    tokens:
      npcName: { source: "a" }
```

- `text` and `translated` are in **slot space**: the same `{name}` on both sides, readable, and the only form the model, validation
  and QC ever see.
- `tokens` maps each slot name to the original text it replaced (`source`). It is written only when there are some, so a game with no
  `namedTokenReplacers` has byte-identical Converted and Mod files.
- Slot names are numbered across a whole cell: the same name with the same source reuses its slot, a different source under the same
  name becomes `{playerTitle2}`. One name therefore means one thing in every fragment of a cell.
- A runtime token with no Chinese next to it sits in the template's literal text, not in a fragment (`<color=#E64D00>a</color>{0}`).
  It is slotted too, and its values are on the template (`templates[].tokens`). It never reaches the model, so it needs no description.
  This is where most `NPC__SelfAction` tokens are; without it they were packaged as a bare `a`.

## Config

```yaml
namedTokenReplacers:
  - literal: "<fName>"        # exact text, or `pattern:` for a regex (exactly one of the two)
    name: firstName           # letters and digits; the model reads {firstName}
    description: the player's first name. Copy it exactly as written.   # added to the prompt when the slot is present
  - literal: gangName
    name: gangName
    restoreAs: "///gangName///"       # put back this text instead of the matched one
  - pattern: '<[^<>|]*\|\|[^<>|]*>'
    name: playerTitle
    restoreFromGlossary: true         # put back the glossary Result of the matched text
```

| Kind (`kind:`) | Restored value | Resolved |
| --- | --- | --- |
| `copy` (default) | the source text | packaging |
| `fixed` (alias: `restoreAs:`) | a configured text | packaging |
| `glossary` (alias: `restoreFromGlossary: true`) | the glossary Result for the source (no entry: the token is left unslotted at export, and packaging fails the line to raw if the entry is gone later) | packaging, so a glossary fix needs no retranslation |
| `translate` | the glossary Result for the source if there is one, otherwise the source translated once by the model and stored in `tokens.<name>.translated` | glossary at packaging; the model call at translation time, before any line is translated |

An explicit `kind` must agree with `restoreAs`/`restoreFromGlossary` (a conflict fails at start-up). `showValue: true` on any kind adds
a prompt line per present slot (`{farewell}` stands for "See you again". Keep the slot as written), so the model has the meaning
without being able to rewrite it. Leave it off where the value must not colour the wording (a gender alternative).

```yaml
  - literal: 后会有期        # a phrase: needs Chinese, so it is slotted only where it appears whole
    name: farewell
    kind: translate
    showValue: true
    description: a fixed parting phrase.
```

### Phrase tokens (`translate`)

A `translate` token's `pattern` may match a phrase, not just a name. The phrase is slotted out of the sentence, translated **once**
(glossary first, then the model, through the ordinary translation path), and put back at packaging, so it reads identically
everywhere and costs one call however often it repeats. Good candidates: boilerplate repeated across many lines, a phrase the game inserts at
runtime, text that must read the same everywhere. Poor candidates: ordinary prose (the model loses the grammar around the slot; use a
glossary entry). Watch a/an, plurals and capitalisation around the slot, and use `showValue`. Add the pattern to the splitter's
`PlaceholderPatterns` as for any token.

How the value is resolved: `TokenValueTranslator` runs before a pass translates any line (`FillTokenValuesAsync`), fills every token that has no glossary
entry and no stored translation, and writes the files it changed. One model call per distinct source and glossary block (the cache key
includes the glossary text that applies to the phrase in that file); a phrase the model cannot translate stays empty, packaging then
falls the line back to raw with a logged reason, and the next run tries again. Merge keeps a stored value for an unchanged source. Lines whose
slots are shown to the model do not share cached translations or duplicates unless the shown values are equal. A phrase is translated on its own, so a
token inside the phrase is not slotted.

The splitter must keep a token inside its sentence: the game's `CompoundFieldSplitterOptions.PlaceholderPatterns` holds the same
patterns, or the sentence is translated in pieces around the token.

## Where each stage reads and writes

| Stage | What happens |
| --- | --- |
| Export (`CsvGameDataWorkflow`, `JsonGameDataWorkflow`, `PrefabTextWorkflow`, `DynamicStringWorkflow`) | `CompoundFieldSplitter.DecomposeSlotted` slots every fragment and the template and writes `tokens`. Export loads `Config.yaml` first when no configuration has been loaded in the process, so an export-only run still slots. |
| Merge (`GameFileHandlingBase.MergeFilesIntoTranslatedAsync`) | Matches on slotted `text`. `tokens` always comes from the export, never from the old data. Converted written before slotting (raw tokens in `text`) matches nothing: re-export, merge, retranslate. |
| Prompt | `StringTokenReplacer.Replace` masks everything else under numbered `{n}` slots and leaves configured slot names alone; `DescribeNamedTokensIn` adds each present slot's description. |
| Validation (`TranslationWorkflow.EvaluateRules`) | In slot space: every slot of the text appears at least as often in the candidate, no invented slot, no unknown `{word}`. A game's `CustomColumnValidator` and the structural check run on the **restored** raw and candidate, so game logic is unchanged. |
| QC | Both sides are slotted text; the corrected text is checked with the same rules, using the whole column's tokens (fragments and template). |
| Pronoun checks | Judge the restored text, so `GameHooks.UnknownGenderPersonTokens` are the game's own tokens. |
| Manual translations | Found by the text with its slots replaced by their sources; a result containing a token's final text is re-slotted; one that leaves a slot of the source out is skipped (logged). |
| Packaging (`PackagingHelpers.RestoreSlots`) | Restores each fragment's `translated` (and the template, and a whole-cell `qcTranslated`) from `tokens`, then rebuilds the cell. A slot that cannot be resolved (no `tokens` entry, an unconfigured name, a glossary entry that is gone, a slot the model invented) sends the line to its raw fallback and logs the reason. Hooks and `onColumnPackaged` get the source text in the game's form. |
| Diagnostics (`TranslationDiagnostics`) | Reports slotted and game-form text, each slot's source and restored value, and the model's result both ways. |

## Offline checks

- `Tests/TokenSlotterTests.cs`: slotting, numbering, restoring, slot problems, round trips.
- `Tests/Workflow/TokenValueTests.cs`: kinds, translate restore, a phrase translated once, the glossary-aware cache key, `showValue`, merge carry-over, and a mocked
  translation run through both schedulers.
- `Tests/Workflow/TokenExportTests.cs`, `TokenPackagingTests.cs`, `TokenRulesTests.cs`: export, merge and idempotency, packaging restore
  and fallbacks, rules in slot space, manual translations, pronoun checks.
- A downstream game keeps a golden packaged output for a fixed set of token and token-free rows (PathOfKungfuOverLlm
  `Tests/PackagingGoldenTests.cs`).

## Rolled out

PathOfKungfuOverLlm runs on slots (see its `docs/investigations/runtime-tokens.md`). Not checked yet: the in-game result.
