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

## Follow-up guidance

When a new QC defect is found, record the reproduction, affected model/prompt, observed output,
root cause, and regression coverage here or in a focused investigation. Update the feature guide
only when the current usage contract changes.
