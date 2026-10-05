# Quality Review Postmortems

The current feature behavior is documented in [the quality review guide](../features/translation-pipeline/quality-review-pass.md).
This page keeps the temporary model-run analysis and corrective history out of that guide.

## Scope

These postmortems came from local-model QC runs in September 2026. They are historical evidence,
not operating instructions. The durable outcomes are reflected in the current workflow, prompts,
validation gates, and the dated ADRs under `docs/architecture/decisions/`.

## Findings retained from the original QC run

1. Omitted subjects or objects survived primary translation when fragments lacked surrounding
   dialogue context. Whole-cell QC and explicit prompt guidance were added to address this.
2. A multiline correction was truncated because the correction regex did not use singleline capture.
3. A validated correction was incorrectly retried because its self-reported score was low. Validated
   corrections are now accepted and surfaced for human review instead.
4. Prompt consistency rules forced corrected scores into a narrow band, then a permissive revision
   forced them high. The rubric was changed to use a graduated confidence scale.
5. `UNTRANSLATED_PINYIN` was misapplied to syllables inside proper nouns. Proper-name handling was
   added to the category guidance.
6. Self-scoring remained unreliable even after rubric changes. The workflow was split into separate
   drafting, verification, and repair responsibilities.
7. `LOST_IDIOM` was applied to plain skill or technique names. Category guidance now excludes ordinary
   descriptive names.
8. QC changed an already translated name back to Pinyin under a catch-all category. Verification
   now treats that regression as invalid regardless of category.
9. Longer corrections sometimes repeated their entire `DEFECT`/`CORRECTED` block. Model stop sequences
   were added for the affected preset.

## QC evaluator model comparison: prompt tuning vs. capability ceiling (2026-09-20)

Two independent `BaseQualityReviewPrompt.txt` rounds targeting specific hard-defect patterns
(placeholder/null-value leaks and name-as-gloss mistranslations in round one; an invented
synonym-pair over-translation pattern plus a forced pre-answer name self-check in round two) were
each verified against a real, freshly-run comparison (see the caching trap below) using
`QualityEvaluatorAssessmentWorkflow` in `DragonHierOverLlm`'s `Files/Goldset/GoldSet.yaml`
(121 detection items as of this writing). Result, both rounds: **Qwen38Qc** (the stronger, general
reasoning-capable model) genuinely improved on every targeted pattern each round. **HyMT2-30B-A3B**
(a translation-specialized model) only ever picked up the purely pattern-matchable defect shape
(the placeholder leak); it did not move at all on either round's semantic-reasoning targets
(name-as-gloss, or the invented-synonym-pair mistranslation) despite both being spelled out
explicitly in the prompt it is given, including one case where the exact failing example was
already baked into the prompt as a worked example.

**Takeaway:** prompt tuning reliably closes a QC-detection gap that is within a candidate model's
underlying reasoning capability, but does not compensate for a capability ceiling below what the
targeted defect requires - two consistent data points across independent, differently-shaped
prompt interventions is enough to treat this as a real finding, not a fluke. When a cheap/fast
model fails to pick up a targeted prompt fix that a stronger model in the same round does pick up,
suspect a capability ceiling before trying a third prompt rewording of the same rule.

**Also surfaced, and followed up the same day:** `Qwen38Qc` (`BaseFiles/Qwen38/Config.yaml`) was
already a quantized model (`hf.co/unsloth/Qwen3.8-27B-GGUF:UD-Q3_K_XL`), not full weight - it had
been treated informally as "the accuracy benchmark" across earlier QC rounds without that being
flagged. A quant-vs-quant comparison of Qwen38 itself found:

- **Quant *method* matters as much as bit-width.** `hf.co/unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_M`
  (Unsloth's Dynamic quant) beat a plain llama.cpp `q4_K_M` build at the same nominal size and
  latency - +0.224 precision, false positives cut from 10 to 4, on an otherwise identical setup.
  When comparing quants of the same model, prefer the Unsloth Dynamic (`UD-*`) build over a
  same-named plain quant from elsewhere when both are available.
- **GPU VRAM ceiling is tighter than raw file size suggests.** On a 16.3 GB-VRAM card, even a
  15.18 GB quant (`UD-IQ4_XS`) was measured spilling slightly to CPU (`ollama ps` showed 8%/92%
  CPU/GPU) - the zero-spill ceiling sits closer to ~14 GB than "under the card's rated VRAM."
  Budget accordingly rather than assuming a quant fits just because its file size is nominally
  under the card's total VRAM.
- **Context window (`num_ctx`) does not speed up an already-loaded model.** Measured directly via
  `/api/chat` (warm, steady-state calls, isolating `eval_duration` from one-time load time): a
  fully-GPU-resident model showed no measurable latency difference between `num_ctx` 8192 and 4096.
  Context size affects the KV-cache allocation ceiling and reload time, not per-token compute cost
  once loaded - reducing it does not "speed up" a model that's already resident on GPU, and does
  not rescue a model whose *weights alone* exceed available VRAM (that only fixable by choosing a
  smaller quant). Right-sizing `num_ctx` to measured real usage is still worth doing (no downside,
  avoids allocating unused KV-cache headroom), just don't expect it to fix a latency problem.
- **Quant choice does not affect prompt-fix generalization.** Both landmark hard cases from the
  prompt-tuning finding above were caught by every quant level tested, including the smallest -
  once a prompt fix generalizes for a model family, it holds across quants, it isn't quant-specific.
  Conversely, categories that stayed weak (a specific honorific-title confusion, and a
  "formatting"-labeled category) were equally weak across every quant tested - not a
  precision/capability gap a bigger quant would fix, but a glossary-coverage or prompt-coverage gap
  needing its own targeted look.

See `docs/plans/qc-evaluator-comparison.md` in `DragonHierOverLlm` for the full live numbers, since
that comparison is tied to that repo's gold set and config.

**Caching trap:** `QualityEvaluatorAssessmentWorkflow` caches each model's `Results.yaml` keyed only
on the gold-set fingerprint, not on prompt content - re-running the assessment after a prompt
change with an unchanged gold set silently reuses stale pre-change results (finishes in under a
second, looks like a successful run). Always delete the model's output directory under
`Files/TestResults/QcEvaluatorAssessment/<model>/` before a round meant to test a prompt or model
config change, or at minimum check `Results.yaml`'s timestamp against the relevant prompt commit
before drawing a conclusion from it.

## Detection latency: num_ctx, not prompt length (2026-10-03)

QC per-call latency had crept up after the 2026-09-26 prompt revisions. A like-for-like A/B on the
DragonHierOverLlm gold set (Qwen38 `UD-IQ4_XS`, single detection, temperature 0, 226 calls each)
separated the two suspects:

| Detection prompt | num_ctx | Median | Mean | p95 |
|---|---|---|---|---|
| current (19KB) | 8192 | 873ms | 1060ms | 1791ms |
| older (13KB) | 8192 | 752ms | 820ms | 1278ms |
| current | 6144 | 694ms | 801ms | 1086ms |
| older | 4096 | 577ms | 685ms | 889ms |

- **The larger context was the main cost.** The same prompt was ~20% faster at 6144 than at 8192,
  with no loss in recall or precision. The prompt growth cost ~14% at a fixed context. At 8192, the
  16GB card sat at ~15.8GB used, and the Ollama runner crashed once mid-run.
- **The detection system prompt is nearly the whole context.** `BaseQualityReviewPrompt.txt` alone
  measured 4585 tokens. Across all ~43k QC columns of the corpus, the per-row part (source, translation
  and glossary) was 87 tokens at p50 and 193 at p90, with a maximum of 865 (worst full prompt 5450). The
  glossary block is too small to be worth trimming. Shrinking the context further means shrinking the
  system prompt.
- `num_ctx` for the Qwen38 preset is now 6144. `QualityReviewWorkflow.CheckDetectionContextBudgetAsync`
  runs at the start of every review pass. It measures the pass's five longest detection prompts via
  Ollama's `prompt_eval_count` (generating one token) and fails fast if any would leave less than
  `DetectionAnswerReserveTokens` free. Previously, a prompt edit that outgrew the context only showed
  up mid-run as 400s and truncated answers (Unscored rows).

## Detection latency: pre-filling the reply (2026-10-03)

Detection time follows the number of tokens generated (~50ms each), not input size. A Pass
(`DEFECTS: NONE`) generates 6 tokens, of which 4 are the fixed label. Step-0 measurement straight
against Ollama (Qwen38 `UD-IQ4_XS`, num_ctx 6144, temperature 0, 16 gold-set calls): decode was a median
44% of `total_duration` on Pass rows (~280ms of ~640ms) and 57% on Defect rows.

**Variant A, pre-filling `DEFECTS:`, is worth enabling, and is now the default**
(`qualityReview.detectionPrefillEnabled: false` opts out). It appends `{"role":"assistant","content":"DEFECTS:"}` to the detection request only (calls 1/2).
Ollama 0.35.1 with `think: false` continues the message cleanly: the reply is `DEFECTS: NONE` with
2 generated tokens, no `<think>` block, no repeated `DEFECTS:`. `QcDetectionResponseParser.Parse`
takes `assumeDefectsPrefix` so a server that returns only the continuation (` NONE`) also parses.
Tests: `Tests/Workflow/QcDetectionPrefillTests.cs`.

Gold set, 226 detection calls per run, single detection, temperature 0, `NUM_PARALLEL=2`, baseline and
prefill alternated (Base1, Pre1, Base2, Pre2):

| Run | Pass median | Defect median | All-rows median | Run time | Recall (all / held-out) | FP |
|---|---|---|---|---|---|---|
| Base1 | 617ms | 842ms | 655ms | 187s | 0.865 (77/89) / 0.873 | 4 |
| Prefill1 | 413ms | 634ms | 449ms | 139s | 0.876 (78/89) / 0.873 | 5 |
| Base2 | 592ms | 796ms | 616ms | 177s | 0.865 / 0.873 | 4 |
| Prefill2 | 415ms | 644ms | 441ms | 141s | 0.876 / 0.873 | 5 |

- **~32% faster** (all-rows median ~635ms -> ~445ms): Pass -205ms, Defect -190ms.
- **Accuracy:** held-out recall is identical (0.873); overall recall +1 (78/89 vs 77/89). Precision 0.940
  vs 0.951. Exactly two verdicts flip, identically in both prefill runs: `a374601ef2bbc3a6` Pass->Defect
  UntranslatedPinyin (a catch) and `b4c303b678cbd79d` [HyMT2-7B] Pass->Defect OtherNamedDefect (one extra
  FP). Prefill is not bit-identical to the baseline, because the first answer token is now conditioned
  on a forced prefix, so a couple of borderline rows move. It stays within the "no worse recall, at most
  ~1 extra FP" bar.
- This session's baseline (recall 0.865, 4 FP) differs slightly from the earlier 0.888/5 FP: the Ollama
  build is now 0.35.1 (was 0.33.1) and `NUM_PARALLEL` is 2. Base2 also had one transient `Unscored` row
  (20ms request failure) that Base1 did not.

**Variant B, short category codes, was tested and rejected.** Built on top of A: only the OUTPUT FORMAT
category line of `BaseQualityReviewPrompt.txt` was rewritten at runtime to ask for codes (`NUM`, `TERM`,
`IDIOM`, `PINYIN`, `DROP`, `FLIP`, `STUTTER`, `SEAM`, `OTHER`, `AWKWARD`; `NONE`/`UNCERTAIN` unchanged), with
the codes mapped back to `QcDefectCategory` in the detection parser only. Prefill-only (A) vs prefill +
codes (B), alternated, 226 calls each:

| Run | Pass median | Defect median | All-rows median | Run time | Recall, all (held-out) | FP |
|---|---|---|---|---|---|---|
| A1 | 420ms | 618ms | 443ms | 139s | 0.888 (0.873) | 5 |
| B1 | 418ms | 506ms | 435ms | 131s | 0.831 (0.831) | 4 |
| A2 | 417ms | 637ms | 441ms | 138s | 0.854 (0.873) | 6 |
| B2 | 414ms | 494ms | 427ms | 127s | 0.831 (0.831) | 4 |

- **Speed gain was small:** ~120ms on Defect rows, ~3% overall (Pass rows have nothing to shorten).
  The model also still wrote full names on some rows (`UNNATURAL_PHRASING`, `LOST_IDIOM`), so the saving is partial.
- **Recall dropped:** held-out 0.873 -> 0.831 (-3 true defects), all-pairs 74/89 vs 76-79/89, identically
  in both B runs. Five defect rows flipped to Pass (including `a374601ef2bbc3a6` and
  `9db5dc00fd337348`); one false positive and one miss were fixed. The names carry meaning for the model.
- The code was removed after the test (this document is the record). Nothing is needed in
  `Config.yaml`.
- **A is not perfectly deterministic under `NUM_PARALLEL=2`:** the earlier two prefill runs were identical,
  but A1 vs A2 here differ on 5 rows (recall 0.888 vs 0.854 overall, held-out 0.873 both, FP 5 vs 6). Treat a
  1-3 row swing between identical runs as noise when judging a variant.

## Correction cost: the verify/repair loop (2026-10-03)

A defect-heavy QC batch (after a glossary update only the ~100 stale columns were eligible) ran at
0.07-0.11 reviewed/s and ~5.5 LLM calls per column. Instrumented run on a fixed 30-column sample (per-call
trace in a sandbox copy, Qwen38 `UD-IQ4_XS`): 122 calls = 30 detection, 25 correction, 46 verification,
21 repair. The refactor and this morning's perf commits were ruled out: yesterday's, 07:37's and
today's builds gave the same corrected/flagged/call counts on the same columns (22/11/130, 23/9/124,
23/11/126).

- **Cost model.** A defective column costs detection + correction + >=1 verification, and every
  rejected verification adds a repair + another verification (up to `maxScoreRepairIterations`, default 2,
  so up to 7 calls).
- **The loop rarely recovers.** First verification accepted 13 of 23 corrections; later verifications
  accepted 2 of 23 (round 2: 0 of 10). ~36% of calls (44 of 122) bought 2 accepted columns.
- **13 of 21 repairs returned the candidate unchanged**, and the loop then re-verified identical text.
- **Verification is noisy.** A rejected column often gets a different "new defect" each round
  (`MEANING_REVERSAL` x9, `DROPPED_CONTENT` x6, `OTHER_NAMED_DEFECT` x4) on text that was fine.
  `SCORE: 0` on a rejection is by design (the prompt says the score is irrelevant then), not a parse failure.
- **Not the rule gate.** `LineValidation` behaviour is unchanged since 2 Oct, the gate rejected 1-2 of 30
  columns, and the post-pass rule re-check reset 3 columns identically in old and new builds. Flags come
  from verification rejections, not rule violations.

**Fix (kept):** `GetLlmVerdictAsync` now stops the loop when a repair returns the candidate unchanged
(`Tests/Workflow/QcRepairLoopTests.cs`), and DragonHierOverLlm sets `qualityReview.maxScoreRepairIterations: 1`.
Same-snapshot A/B, 27-28 columns reviewed each: baseline 86 and 90 calls (12 and 14 corrected, 123 s and
117 s); no-op stop 77 and 76 calls (13 and 13 corrected, 101 s and 103 s), about -13% calls and -14% time;
adding `maxScoreRepairIterations: 1` gave 74 and 75 calls (12 and 14 corrected). Outcomes are within run noise.
An earlier A/B was discarded because its runs did not start from identical data; when comparing runs, check
the "already reviewed and unchanged" count in the log matches.

### Why first verifications reject, and the pre-verification gate (2026-10-03, evening)

Same sandbox and snapshot (42727 unchanged, 28 columns reviewed), production config (`maxScoreRepairIterations: 1`,
detection prefill on), with a JSONL trace of every call's full prompt and reply. The 13 first-verification
rejections across two baseline runs, read by hand:

- **9 were real bad corrections:** 5 dropped a meaningful placeholder (`{3}` = who wins, `{1}`), 1 left raw
  Chinese (`R-r-饶`), 3 produced garbled English. The verifier caught them, but often with the wrong category
  name (a dropped `{3}` reported as `MEANING_REVERSAL`), so the repair got no usable target and 3 of the 5
  placeholder repairs came back unchanged. The validation gate then rejected the column anyway.
- **4 were verifier false rejections** on the same 2 items both runs: the correction removed English with no
  counterpart in SOURCE (an invented "Senior Disciple", a "Master," for 爷) and the verifier called it
  `DROPPED_CONTENT`.
- **0 corrections were identical to the original translation**, and 0 (source, candidate) pairs were verified
  twice in 8 runs, so skipping or caching those would save nothing (the no-op stop and review cache cover it).

**Fix (kept, on by default): `qualityReview.preVerificationGateEnabled`.** `GetLlmVerdictAsync` runs the column's
validation gate (`EvaluateQcCandidate`, the same check the final accept path runs) on each candidate before
verifying it. A failing candidate skips verification and goes to a repair whose prompt carries the gate's
reason as `RULE CHECK FAILURE` (e.g. "Restore `{3}` to the translation..."); it shares the repair budget, and
a blank reason or unchanged repair stops the loop. Tests: `Tests/Workflow/QcPreVerificationGateTests.cs`.

| Variant (2 runs each) | Calls/col | 1st verification accepted | Corrected | Rejected by gate | Flagged | Seconds* |
|---|---|---|---|---|---|---|
| Baseline | 2.82 / 2.75 | 13/20, 14/20 (68%) | 14 / 13 | 6 / 7 | 2 / 2 | 143 / 110 |
| Gate | 2.71 / 2.71 | 14/18, 14/17 (80%) | 18 / 17 | 2 / 3 | 4 / 3 | 103 / 101 |
| Gate + verifier DROPPED_CONTENT wording | 2.68 / 2.68 | 14/18, 14/17 | 18 / 17 | 2 / 3 | 3 / 3 | 99 / 98 |
| Gate + wording + verification temperature 0 | 2.68 / 2.71 | 14/17, 14/17 | 17 / 17 | 3 / 3 | 3 / 3 | 98 / 98 |

\*The game was running on the same GPU during these runs, so seconds are indicative only; call counts are not affected.

The gate converts ~4 of 28 columns from gate-rejected to corrected at no extra calls. The gate-guided repairs read
correctly ("ultimately proving that {3} is the superior fighter", negative signs restored on a stat list); the
`{1}` repair was placed awkwardly ("This {0} is {1} the supreme sanctuary") but is better than the original. Flagged
rises by ~1 because more columns now reach verification and meet its own (largely legitimate) objections, e.g. the
glossary term "Knockback" missing.

**Tried and rejected:** stating in the verification prompt that DROPPED_CONTENT is judged against SOURCE did not
change either false rejection; verification at temperature 0 gave identical counts (the remaining rejections are
stable verifier judgements, not sampling noise). Neither was kept. Reply prefill for correction/verification
was not measured: it saves ~3 label tokens per call (~2% of LLM time), below this harness's +-10% run noise.

**Side finding:** one column (the save-backup-path tip) failed the gate with an *empty* reason. Fixed below.

### Follow-up fixes and verifier evidence (2026-10-03, night)

Same sandbox/snapshot, GPU free this time.

- **Blank gate reasons.** `LineValidation.CheckTransalationSuccessful` had six checks that fail without adding a
  correction prompt (invalid phrase, two length blow-ups, unbalanced color tags, short-name length, empty
  source), so `EvaluateRules` reported `""`. They now record `ValidationResult.SilentFailures`, which the
  structural reason falls back to (translation retry prompts are unchanged). The save-path tip's actual cause:
  the `"\U"` invalid phrase (meant for stray unicode escapes) matched `C:\Users`. An invalid phrase the source
  itself contains is no longer a failure. Tests: `Tests/LineValidationSilentFailureTests.cs`.
- **Newlines in corrections.** The correction/repair prompts ask for a literal two-character `\n`, which
  stored visible `\n` text for sources with real line breaks (and models sometimes answered with a real break
  for literal-`\n` sources). `MatchSourceNewlines` now normalises every correction/repair to SOURCE's form.
  Tests: `Tests/Workflow/QcNewlineConventionTests.cs`.
- **Verifier evidence (`qualityReview.verificationEvidenceEnabled`, default false; DragonHierOverLlm enables
  it).** Verification uses `BaseQualityReviewVerificationEvidencePrompt.txt`, which adds an
  `EVIDENCE: CATEGORY: "quote"` line. `QcVerificationResponseParser.FilterByEvidence` overrules a claim whose
  quote is punctuation-only or not in SOURCE (DROPPED_CONTENT) / SOURCE or the candidate (others); a claim with
  no entry is kept. Surviving quotes reach the repair as `VERIFIER EVIDENCE`. A rejection that survives keeps
  score 0: the first version used the evidence prompt's real grade, which let still-rejected candidates
  (scored 75) clear `minAcceptableScore` unflagged - caught in the traces and fixed before measuring again.
  Tests: `Tests/Workflow/QcVerificationEvidenceTests.cs`.

| Variant (2 runs each) | Calls/col | Corrected | Rejected by gate | Flagged | Later verifications accepted | LLM ms |
|---|---|---|---|---|---|---|
| Original baseline (above) | 2.82 / 2.75 | 14 / 13 | 6 / 7 | 2 / 2 | 4/4, 1/3 | 237k / 206k* |
| New defaults (gate + blank-reason + newline fixes) | 2.79 / 2.64 | 19 / 18 | 1 / 2 | 3 / 2 | 1/2, 0/0 | 196k / 179k |
| New defaults + verifier evidence | 2.71 / 2.75 | 17 / 19 | 3 / 1 | 1 / 1 | 2/2, 2/3 | 195k / 192k |

\*Game running on the GPU for the original baseline.

What evidence changed, read from the traces: the recurring "Senior Disciple" false rejection quoted only `"。"`
and was overruled in both runs (accepted at 85); quoted repairs recovered columns the bare-category repair did
not ("掌、掌门" -> "Sect Leader, Sect Leader, big, big trouble!"; "dealt the Knockback" -> the glossary-consistent
"knocked back"). The 爷 line stays rejected with a real quote and is flagged, as it should be. Raw first-verification
acceptance did not move (the gain is in overruled claims and repairs that land); call count and LLM time unchanged.

## Follow-up guidance

When a new QC defect is found, record the reproduction, affected model/prompt, observed output,
root cause, and regression coverage here or in a focused investigation. Update the feature guide
only when the current usage contract changes.
