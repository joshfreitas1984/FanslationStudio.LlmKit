# Plan: move the QC gold set and regression into LlmKit

Status: **implemented** (all phases). Phase 1 (workstream A: rename, loader shims, vocabulary lint test) implemented 2026-10-09 in LlmKit and the three QC-using games (uncommitted). Phase 2 (workstream E step 1) implemented 2026-10-09: `Tests/Configuration/PresetGlossaryLintTests.cs` plus the obvious `CommonStats` fixes (removed 阴/阳/刚/柔/毒, bad alternatives), the 化境 typo, a dead 两 entry and a stray YAML item in `Phonetics`; judgment-call entries (太阳, 承让了, 机关, ...) wait for the corpus audit. Phase 3 (B1-B2) implemented 2026-10-09: `FanslationStudio.LlmKit.Assessments` host project (manual tests gated by `LLMKIT_ASSESSMENTS=1`), schema-v2 gold set (`game`, `sourceFile`, `glossary` snapshot per case), and Dragon Heir's 251 cases imported with snapshots verified to reproduce the live glossary prompt; the live `Assess QC models` run from LlmKit (2026-10-09, snapshot gold set) reproduced Dragon Heir's 2026-10-07 numbers within noise (correction-safety 86.4% vs 84.1%, detection accuracy 72.4% vs 73.0%, recall 53.4% vs 54.0%, precision 90.5% vs 91.6%); B4 (retiring the game copies) is still open. Phase 4 (C1, C3 part) implemented 2026-10-09: `Games.yaml` registry, read-only `GameCorpus`, and deterministic scans (`GlossaryScans`: per-entry match/hit/shadowed stats, preset-change impact, detector blast radius) with CI-safe unit tests and manual cross-game reports (`Files/TestResults/Scans/`); the detector scan has the function but no concrete detector wired yet. Phase 5 (E steps 2-4) first pass implemented 2026-10-09: corpus audit plus the evidenced preset fixes, findings in [`investigations/preset-glossary-audit-2026-10.md`](../investigations/preset-glossary-audit-2026-10.md); a second review pass settled every open decision (single-game nouns moved to their games, idiom alternatives, phrases allowed in the preset with good alternatives). Phase 6 implemented 2026-10-09: B3 (`translationAssessment.source: goldSet` translates the gold set's own sources with each case's glossary snapshot, plus deterministic per-case findings `LeftoverCjk`/`SelfReferenceLost`; one Qwen3.8 run: 203 of 251 completed, 2 detector-flagged, 6.9 minutes; model comparison deliberately dropped, the host config lists Qwen3.8 only) and C2 (`Mine gold-set candidates` writes up to 8 candidates per game and defect category with game, sourceFile and glossary snapshot filled in; first run 90 candidates, labelling still a human step). Phase 7 implemented: glossary scope rubric in `features/translation-pipeline/glossary.md` and the `add-glossary-fix` and `expand-goldset` skills, with `investigate-qc-issue`, `new-translation-project` and `add-character-gender-context` updated. Phase 8 (B4) implemented 2026-10-09: DragonHierOverLlm's `Goldset/`, `translationAssessment` and `qualityControlAssessment` config, `AssessmentWorkflowTests.cs` and the `expand-qc-goldset` skill were removed after confirming all 251 cases and all 103 pinned sources exist in LlmKit's gold set; the QC comparison scripts moved to `FanslationStudio.LlmKit.Assessments/Scripts/` and four assessment investigations moved to `docs/investigations/`. Its `TestResults/` archives stay in place (untracked).

Audience: LlmKit maintainers and the agents working in this repo and the "over LLM" game repos.

## Goal

Every time a game shows a translation or QC defect and we fix it, the fix should be **proved against
every other game**, not just the game that found it. That needs four things, which this plan delivers
as five workstreams:

| | Workstream | Outcome |
| --- | --- | --- |
| A | Consistent QC vocabulary | One name for the feature, in code, config, tests, docs and skills. |
| B | Assessment host + self-contained gold set | LlmKit runs the model assessments and the QC regression. Games do not. |
| C | Cross-game access, mining and impact scans | LlmKit reads each game's glossary and failures, mines new cases, and shows what a change does elsewhere. |
| D | Glossary scope rubric + skills | A QC fix goes to the preset glossary or the game glossary on purpose, not by habit. |
| E | Preset glossary audit | The shipped presets stop injecting wrong or game-specific terms into every game. |

Why now: the 2026-10-07 flagged-queue review (see
[`investigations/quality-review-postmortems.md`](../investigations/quality-review-postmortems.md), which
workstream A renames, once the findings are consolidated) traced most "QC defects" to causes that were invisible from a single game: two QC
prompts that contradicted each other, a preset entry (三七 → "Sanqi") applied inside idioms, and a
self-reference pattern QC marked `Passed`. Each was found in Dragon Heir and then needed a manual check of
LegendOfMortal to see the blast radius.

## Principles

1. **LlmKit owns assessment.** Model assessments, the gold set and the QC evaluator run from this repo.
   Games carry no assessment config, tests or gold-set copy.
2. **A case is self-contained.** A gold case holds the source, the translation under test, its labels and
   the glossary it was judged with, so it gives the same verdict with no game present.
3. **Read-only reach into games.** LlmKit may read each game's `Config.yaml`, `Glossary/`, `Converted/` and
   `ManualTranslations.yaml`. It never writes into a game repo except through an explicit, reviewed
   "apply" step.
4. **Deterministic first.** Anything that can be a pure xUnit test (lint, shadowing, detector blast radius)
   is one and runs in CI. Only the LLM-backed runs stay manual.
5. **Respect the golden rule** ([`AGENTS.md`](../../AGENTS.md)): persisted YAML keys keep deserializing.
   Nothing here renames a key that exists in a game's `Converted/*.yaml`.

## Current state (verified 2026-10-07)

### Where the gold set and assessments live

- The workflows are already in LlmKit: `TranslationAssessmentWorkflow` and `QualityEvaluatorAssessmentWorkflow`
  (both `public static ... RunAsync`).
- Everything they need is in **Dragon Heir**: `Files/Goldset/GoldSet.yaml` (207 detection items + 44 correction
  samples), the `translationAssessment` / `qualityEvaluatorAssessment` sections of `Files/Config.yaml` (model
  lists, sample size and seed, **103 pinned sources**), `Tests/AssessmentWorkflowTests.cs`, and the result
  archives under `Files/TestResults/`.
- Gold-set provenance is **entirely Dragon Heir** (`PlotData`, `dynamicStrings*`, earlier Dragon Heir
  model-assessment runs). That is not a problem for the design (the set is for Chinese→English wuxia, any game
  may contribute), but it means no other game's cases exist yet.
- The QC evaluator takes its glossary from the **running game's config** (`config.Runtime.GlossaryLines`), so a
  case's verdict depends on which game's glossary is loaded. This is the main coupling to remove.
- The translation assessment samples from a game's `Raw/Export`, so it cannot run without a game.

### The five game repos

| Repo | Converted | Glossary files | Flagged for QC | Notes |
| --- | --- | --- | --- | --- |
| `DragonHierOverLlm` | 44 | 12 | 4 files (≈47 items after the 10-07 work) | Owns the gold set today. |
| `LegendOfMortalOverLlm` | 48 | 1 | 32 files, 600 items (547 score 0) | Same verifier-harshness pattern; own `真人` entry overrides the preset. |
| `WanXiangOverLlm` | 38 | 1 | 15 files (not analysed yet) | |
| `Xyzj2OverLlm` | 86 | 0 | none (no QC run) | Old version; deliberately **not** registered for scans. |
| `FateseekerOverLlm` | 0 | 1 | none | Not yet translated; deliberately **not** registered for scans. |

### QC naming today

`QualityCheck` does not appear anywhere. The real spread is:

| Term | Where | Count (files, excluding data) |
| --- | --- | --- |
| `QualityReview` | `QualityReviewWorkflow`, `QualityReviewConfig`, `qualityReview:` config key, `BaseQualityReview*Prompt.txt`, `docs/features/quality-review/` | 61 in LlmKit, 3–8 per game |
| `QualityControl` | `QualityControlWorkflowTests` in **three game repos**, skill text, downstream test docs | 3 LlmKit, 2–7 per game |
| `QualityEvaluator` / `QcEvaluator` | `QualityEvaluatorAssessmentWorkflow`, `qualityEvaluatorAssessment:` key, `QcEvaluator*Tests` | 8 LlmKit, 3 Dragon Heir |
| `Qc` prefix | `QcStatus`, `QcDefectCategory*`, `QcDetectionResult`, `Qc*Tests`, persisted keys `qcStatus` / `qcTranslated` / `qcQualityScore` / `flaggedForQcReview` | 66 LlmKit, 5–13 per game |
| "QC" in prose / skill names | `investigate-qc-issue`, `expand-qc-goldset`, 24 docs saying "QC pass" | 75 LlmKit |

Docs also hold three near-empty `quality-review-pass.md` stubs (4–5 lines each) next to the real 757-line
`features/translation-pipeline/quality-review-pass.md`.

### Preset glossary today

13 files, 373 entries (`BaseFiles/ChineseGlossary/`), loaded in full unless a game lists a type under
`chineseGlossaryTypesToSupress` (Dragon Heir suppresses `Phonetics`). The file set is the
`ChineseGlossaryTypes` enum, so adding, removing or renaming a preset file is a config-compatibility change.

## Workstream A: one QC vocabulary

**Decided (2026-10-07): the long form is `Quality Control`, the short form is `Qc`, and each means one thing.**
`QualityReview` and every other spelling go away. An abbreviation with two possible expansions (`QC` for both
"quality control" and "quality check"/"quality review") is the problem being fixed, so the plan keeps exactly one:

| Use | Spelling |
| --- | --- |
| Prose, headings, docs | "Quality Control" (first use) then "QC" |
| Types, workflows, config classes | `QualityControl*` (`QualityControlWorkflow`, `QualityControlConfig`, `QualityControlHelpers`) |
| Config keys | `qualityControl:`, `qualityControlAssessment:` |
| Prompt names | `BaseQualityControl*Prompt` |
| Persisted data and short identifiers | `Qc*` / `qc*` (`QcStatus`, `qcTranslated`, `flaggedForQcReview`) |
| Banned everywhere | "Quality Review", `QualityReview*`, "Quality Check", `QualityCheck*`, `QualityEvaluator*` |

Because `Qc` already is the data prefix, **no persisted data changes**: `qcStatus`, `qcTranslated`,
`qcQualityScore`, `flaggedForQcReview` and the rest keep deserializing in every game's `Converted/`. That
satisfies the golden rule in [`AGENTS.md`](../../AGENTS.md).

### What the rename touches (verified 2026-10-07)

| Surface | Today | Becomes | Compatibility |
| --- | --- | --- | --- |
| Types | `QualityReviewWorkflow` (128 uses), `QualityReviewConfig` (64), `QualityReviewHelpers` (35), `QualityReviewHelpersTests`, `QualityReviewFiveCallFlowTests` | `QualityControl*` | None needed: the games build against LlmKit by project reference, so the rename is one atomic change across repos. |
| `LlmConfig.QualityReview` property and `qualityReview:` config key | in the `Config.yaml` of Dragon Heir, LegendOfMortal and WanXiang | `QualityControl` / `qualityControl:` | **Loader shim:** keep reading a legacy `qualityReview:` key for one release (map onto `QualityControl`, warn once), and migrate the three configs in the same change. |
| Prompt names | five `BaseQualityReview*Prompt` files, referenced as string keys in code | `BaseQualityControl*Prompt` | No game overrides these today (checked). **Loader safety:** warn when a game's `customPromptsPath` holds a legacy `BaseQualityReview*` file, since it would otherwise silently stop applying. |
| Assessment | `QualityEvaluatorAssessmentWorkflow`, `qualityEvaluatorAssessment:`, `QcEvaluator*Tests` | `QualityControlAssessmentWorkflow`, `qualityControlAssessment:`, `QcAssessment*Tests` | Pairs with `TranslationAssessment`. Only Dragon Heir has the section, and workstream B removes it from games. |
| Downstream tests | `QualityControlWorkflowTests` in Dragon Heir, LegendOfMortal and WanXiang | unchanged | Already the target name. |
| Docs | `docs/features/quality-review/`, `quality-review-pass.md`, `investigations/quality-review-postmortems.md`, 29 pages saying "quality review" | `quality-control/`, `quality-control-pass.md`, `quality-control-postmortems.md`, "Quality Control" | Repoint every link, `docs/README.md` and `KNOWN_ISSUES.md`. |
| Duplicate stubs | three 4–5 line `quality-review-pass.md` pages | removed | Links repointed to the real page. |
| ADR file names (`260918-004-quality-review-...`) | history | unchanged | Allow-listed in the lint test. |
| Skills | `expand-qc-goldset`; `investigate-qc-issue` | `expand-goldset` (it becomes cross-game, workstream C); `investigate-qc-issue` unchanged | `QC` is the short form. |

### Steps

1. **Rename in LlmKit** with a scripted, reviewable pass (type and file renames first, then string keys, then
   prose), building and running the unit suite between steps. Add the config-key and prompt-name shims.
2. **Rename in the games** in the same change window: type references, the `qualityReview:` key, doc and skill
   text. Dragon Heir 8 files, LegendOfMortal 3, WanXiang 5 (counts of files mentioning the old name).
3. **Add a vocabulary lint test** (LlmKit `Tests/`, no LLM) that scans source, docs and skills for the banned
   terms, with an allow-list for ADR history and the legacy-key shim.
4. **Add a "Terminology" section** to the renamed `quality-control-pass.md` defining `Quality Control`, `QC`,
   `anchor`, `flagged` and `score gate`, so the one-meaning rule is written down.

**Do this first.** Workstreams B and C move code and docs; renaming afterwards would touch them twice.

Acceptance: the lint test is green in LlmKit; LlmKit and every game solution build; a game that still has a
legacy `qualityReview:` key loads with a single warning; no game or doc says "Quality Review".

## Workstream B: assessment host and self-contained gold set

### B1. Host project

A new project in the LlmKit solution, `FanslationStudio.LlmKit.Assessments` (xUnit, manual `Fact`s like the
game numbered tests), with its own working directory:

```
FanslationStudio.LlmKit.Assessments/
  Files/
    Config.yaml                 models, qualityControl, qualityControlAssessment, translationAssessment
    GoldSets/
      ChineseToEnglishWuxia.yaml
    TestResults/                gitignored (results + archives)
  AssessmentTests.cs            "Assess QC models", "Assess translation models"
```

`Config.yaml` is the Dragon Heir model/QC sections minus game-only keys (`lineContextEnabled`, resizers,
packaging). The workflows run unchanged against it, because `ConfigurationExtensions.GetConfiguration` already
works from any working directory.

### B2. Gold-set schema v2 (self-contained cases)

Add optional fields (safe defaults, per the golden rule), bump `schemaVersion` to 2:

```yaml
- sampleId: 4700c99277962e5b
  game: DragonHierOverLlm            # which game the case came from
  sourceFile: dynamicStrings.txt     # for 'only:'-scoped glossary entries
  source: "…"
  currentTranslation: "…"
  label: Defect
  glossary:                          # snapshot of the entries that applied when it was judged
    - raw: 赵点检
      result: Marshal Zhao
  ...
```

- `MaskSample` uses the case's `glossary` when present and falls back to the loaded config's glossary
  otherwise (so old cases and quick local runs still work).
- **Migration:** one scripted pass over the existing 251 cases that loads Dragon Heir's merged glossary
  (preset + workspace, via `GetConfiguration`) and snapshots the entries that match each source, applying
  `FindShadowedByLongerMatch` and the `only:`/`exclude:` file scoping from `sourceFile`. Verify by re-running
  the QC evaluator before and after: verdicts should match except where the old run depended on a glossary
  entry the snapshot dropped.

### B3. Translation assessment without a game

Add a `source: goldSet` sampling mode: the corpus is the gold set's own sources (detection items and correction
samples), translated with each case's glossary snapshot set as the run's glossary. It replaces both the random
200-sample draw and the 103-line pin list, since every pinned line is already a gold item (verified: all 103
unique pins match a gold source, 68 of them as Defect items). Keep random game sampling as an optional extra when a game
registry entry is supplied (workstream C).

Open point: the translation assessment records speed and structural validity, not correctness. For regression we
also want a pass/fail per case. Proposed: run the deterministic detectors (`LosesSelfReference`, pronoun checks,
leftover-CJK, placeholder checks) on each output and report per-case results, and optionally feed outputs through
QC detection. This is a follow-up inside B, after B1–B3 land.

### B4. Retire the game copies

After B1–B3 verify: remove `translationAssessment`, `qualityEvaluatorAssessment` and the pinned list from each
game's `Config.yaml`, delete `Files/Goldset/` and `Tests/AssessmentWorkflowTests.cs`, update
`downstream-config-shape.md` and `downstream-test-organization.md`, and move the assessment investigations
(`qc-evaluator-model-selection.md`, `qc-qualityscore-noise-investigation.md`, translation-prompt-tuning notes)
from Dragon Heir's `docs/investigations/` into LlmKit. Dragon Heir's `TestResults/` archives stay where they are
(untracked history).

Acceptance: `Assess QC models` run from LlmKit reproduces the last Dragon Heir numbers within run-to-run noise
(correction-safety 84% on the 2026-10-07 set, ±2 items); no game repo references the gold set.

## Workstream C: cross-game access, mining and impact scans

### C1. Game registry

`FanslationStudio.LlmKit.Assessments/Files/Games.yaml`:

```yaml
games:
  - name: DragonHierOverLlm
    path: ../../../DragonHierOverLlm/Files
  - name: LegendOfMortalOverLlm
    path: ../../../LegendOfMortalOverLlm/Files
  - name: WanXiangOverLlm
    path: ../../../WanXiangOverLlm/Files
```

A `GameCorpus` helper loads a game with `GetConfiguration(path)` (so preset + workspace glossary merge exactly
as in the game) and exposes `Converted` lines, the glossary and flagged items. A missing sibling checkout is a
skipped game with a warning, never a failure, so CI and other machines still work.

### C2. Mining

Generalise the `expand-qc-goldset` skill to every registered game: enumerate each game's flagged and
FailedValidation items, group by defect category and by what the change touched, and emit **candidate** gold
cases (with `game`, `sourceFile` and the glossary snapshot filled in automatically) for human labelling. A human
still writes the label, category and review note. Seed run: LegendOfMortal's 600 flagged items (family-title
self-reference, 老夫, the verifier-harshness cases) and WanXiang's 15 flagged files.

### C3. Impact scans (deterministic, CI-safe where a corpus is present)

Pure functions over each game's data, reported per game and as xUnit tests with thresholds:

| Scan | Answers | Example from this project |
| --- | --- | --- |
| Glossary overlap | Which terms are injected only inside a longer term? Which containment conflicts exist? | 三七 inside 三七开; 命中 inside 命中率 |
| Preset-change impact | How many existing translations use the old result of a changed preset entry? | 真人: 219 of 228 LegendOfMortal lines said "Immortal" |
| Detector blast radius | How many lines would a new detector flag, and what are they? | `LosesSelfReference`: 88 lines flagged in Dragon Heir; 2 in LegendOfMortal (5 once family titles are added) |
| Corpus miss-rate | Per glossary entry: lines whose source matched but whose translation lacks the result | Would have ranked 三七 near the top |

### C4. The regression gate

| If a change touches… | Run before merging |
| --- | --- |
| a QC prompt or the QC engine | QC evaluator on the full gold set; compare with the last archived run |
| the translator prompt | translation assessment (gold mode) + detector results; spot-read changed cases |
| a preset glossary entry | preset-change impact + overlap scan across all games; QC evaluator |
| a detector or validator | detector blast-radius scan across all games |
| the glossary engine | overlap scan + LlmKit unit suite |

Record the run in the commit message or a short entry under `docs/investigations/`, not in auto-loaded files.

Acceptance: one command per scan, run against all five games from LlmKit alone; a change to a preset entry
produces a per-game impact table without opening a game repo's tests.

## Workstream D: glossary scope rubric and skills

### The rubric

When a QC fix is "add a glossary line", decide **where it lives** before writing it.

| Ask | Preset (LlmKit) | Game glossary |
| --- | --- | --- |
| Is it a proper noun (person, place, faction, skill, item, event)? | No (sect/place *types* only) | **Yes** (慕容 → Murong, 参苓白术散) |
| Would any wuxia game use the same translation? | **Yes** (掌门 → Sect Leader, 少侠 → Young Hero) | No |
| Is it a game UI/stat label? | Only if two or more games use the same term | **Yes** by default |
| Does the same source need a different translation in another game? | No (preset is shared) | **Yes**, as an override |
| Is it a phrase or a sentence rather than a term? | **Never** | Only as a `ManualTranslations` override for one exact line |
| Is it one character, or likely to sit inside names/idioms? | **Never** | Only with `only:` scoping |

Before adding or changing a **preset** line: run the preset-change impact and overlap scans (C3) and note the
per-game counts. A preset change that alters translations already shipped in a game needs a decision about
re-translation or a mechanical swap, as the 真人 → Daoist change did. When a game must differ, override it in that
game's glossary and say why in a comment.

Document the rubric in [`features/translation-pipeline/glossary.md`](../features/translation-pipeline/glossary.md)
and link it from the skills.

### Skills to update

| Skill (LlmKit and the game copies) | Change |
| --- | --- |
| `investigate-qc-issue` | After the root cause, a step "if the fix is a glossary line, apply the rubric". Record the decision. Prefer a prompt/engine cause over a glossary patch (existing guidance, made explicit). |
| `expand-qc-goldset` → `expand-goldset` | Cross-game mining (C2); snapshot the glossary into each case. |
| `game-update-refresh`, `new-translation-project` | Point glossary steps at the rubric; new projects get no assessment config. |
| `add-character-gender-context` | Note that character names and genders are always game-specific. |
| new `add-glossary-fix` (small) | Runs the rubric, the scans, and writes to the right file. Used by the others. |

The skill copies live in each game repo (`new-translation-project` copies LlmKit's), so the update needs a sync
step; `Tests/SkillSyncTests.cs` already exists for this and should be extended to cover the new skills.

## Workstream E: preset glossary audit

### Method

1. **Static lint** (xUnit, no corpus): no single-character `raw`; no duplicate raw across files; no `result`
   containing CJK; `allowalt` entries not truncated or empty; `result` spelling check against a small word list;
   no entry whose `direct`/`literal` contradicts `result`; casing convention per file.
2. **Corpus audit** across the registered games (C3): per entry, how many source lines match, how often the
   result appears, which games match at all. An entry matched in only one game is a *move to that game* candidate;
   an entry never matched is a *dead* candidate; a high miss rate is a *wrong or contested* candidate.
3. **Conflict analysis** with the extended containment check (already implemented for `badtrans: false`
   entries with a 2+ character raw).
4. **Human review** of each flagged entry, file by file, recording keep / correct / move / remove.

### First-pass findings (read from the three files you named; to be confirmed by the audit)

`CommonStats` (44 entries) reads as one game's combat-stat vocabulary rather than generic wuxia:

- Single-character entries 阴 → Yin, 阳 → Yang, 刚 → Hard, 柔 → Soft, 毒 → Poison. `badtrans: false` stops the
  rule check but they are still injected into prompts, so 刚 → "Hard" reaches 熊刚 and 金刚密宗. The overlap scan
  counted 15 containment overlaps from `CommonStats` entries. (About 2,400 more come from Dragon Heir's own
  `NameData` name-syllable entries; those are intentional game-glossary entries and the shadowing rule handles
  them, so the single-character rule is about the *preset*, not game glossaries.)
- 太阳 → "Great Yang": 太阳 is ordinarily "the sun". 生命 → "Health" is ordinarily "life".
- 命中 and 命中率 both → "Hit Rate" (and 命中率 allows "Accuracy", which LegendOfMortal's own glossary uses for
  it).
- Contested stat renderings: 根骨 → Potential, 气势 → Momentum (its own `direct` says "Qi force"), 体魄 → Resilience,
  御心 → Discipline, 化解 → Counter, 精神 → Willpower.
- Bad alternatives: `专注 allowalt: Concentratio` (truncated), `真气 allowalt: grand`, `迷惑 allowalt: mesmerizing`,
  `外伤 allowalt: Extra Damage | Damage`.

`WuxiaTerms` (13):

- 承让了 → "Thank you for letting me win" is a dialogue phrase, not a term. It produced a gold item.
- 河图 → "River Map" next to 河出马图 → "Hetu Luoshu" (inconsistent; the latter is the book 河图洛书).
- 机关 has `direct: Organization`, which is a different sense (the word for a government organ).
- 马帮 → Horse Gang and 盐帮 → Salt Gang are faction names that belong in `Sects` or a game glossary.

`XianxiaTerms` (10):

- 海色宝会 → "Sea Treasure Gathering" is an event name from one game.
- 空军 → "Sky Warrior" translates "air force" as a warrior; game-specific at best.
- 化境 → "Transcendant" (misspelled).
- 化神 → "Soul Formation" is not the standard rendering (usually Deity or Spirit Transformation), and 元婴 → Nascent
  Soul sits next to it.
- 真人 → Daoist (already fixed on 2026-10-07; the five Dragon Heir nicknames and 299 LegendOfMortal translation
  fields followed).

### Deliverables

- A reviewed table per preset file (keep / correct / move / remove, with the reason and per-game match counts).
- The corrections and removals, in one commit per file.
- Moves land in the owning game's glossary through the C-side tooling (a reviewed "apply" step).
- The lint tests (step 1), kept permanently in `Tests/`.
- Compatibility note: removing an entry is safe; removing or renaming a **file** changes
  `ChineseGlossaryTypes`, which games reference by name in `chineseGlossaryTypesToSupress`. Prefer emptying a
  file to deleting it, and announce any enum change in `downstream-config-shape.md`.

Do `CommonStats` first: single-character entries are the largest source of noise and need no corpus to fix.

## Sequencing

| Phase | Work | Depends on |
| --- | --- | --- |
| 1 | **A** naming + lint test | none |
| 2 | **E step 1** static lint and the obvious preset fixes (single characters, typos, bad alts) | none, can run alongside phase 1 |
| 3 | **B1–B2** host project, schema v2, glossary snapshot migration, verify against the last numbers | 1 |
| 4 | **C1, C3** game registry and deterministic scans | 3 |
| 5 | **E steps 2–4** corpus audit, conflict analysis and review using the scans | 4 |
| 6 | **B3** translation assessment on the gold set; **C2** mining, seeded from LegendOfMortal and WanXiang | 4 |
| 7 | **D** rubric, skills and `add-glossary-fix`, informed by what phase 5 found | 5 |
| 8 | **B4** retire the game copies; consolidate docs (one pass, per the repo's documentation rule) | 3, 6 |

Each phase ends with: LlmKit unit suite green, every game's solution builds, and a short note of what was run
and what it showed.

## Risks and open questions

- **Atomic rename (A).** The types and prompt names change in lockstep across LlmKit and the games (project
  references), so a half-applied rename breaks every game's build. Do it as one change window: LlmKit first, then each
  game fixed to compile, then the legacy-key and prompt-name warnings checked on a game that has not been
  migrated.
- **Gold-set drift.** Snapshotting the glossary freezes what a case was judged with. When a preset entry is later
  corrected, affected cases should be re-snapshotted deliberately, and the case's `labelVersion` bumped, so a
  correct preset fix never shows up as a regression.
- **Run-to-run noise.** The QC evaluator varies by about 1–2 items between identical runs (see the memory notes
  on 屁滚尿流 and 莫慌). Gates compare to the archived run with that tolerance, not exactly.
- **Sibling-path coupling.** `Games.yaml` assumes the sibling checkout layout. Treat a missing game as "skipped"
  everywhere.
- **Cross-game prompt tuning.** One QC model and prompt serves all games. If a fix helps one game and hurts
  another, the gold set (with cases from both) is the arbiter; a game-specific prompt override is the fallback and
  should be rare.
- **Translation-assessment correctness (B3).** Detector-based pass/fail only covers known defect shapes. Decide
  whether QC detection on the outputs is worth its cost.
- **Skill copies.** Skills are duplicated per game; the sync mechanism must cover the renamed and new skills.

## What was verified for this plan

Counts and file lists above were read from the repos on 2026-10-07: naming occurrences by `grep` over source,
YAML, markdown and text (excluding `Converted/`, build output and `TestResults/`); preset sizes and the three
audited files by loading the YAML; game repo contents by listing each `Files/` directory; gold-set provenance
and pin coverage by loading `GoldSet.yaml` and `Config.yaml`; LegendOfMortal's flagged counts and the 真人 usage
from its `Converted/`. The preset findings are a first read of three files, not a full audit.
