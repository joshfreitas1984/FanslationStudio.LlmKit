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

## Follow-up guidance

When a new QC defect is found, record the reproduction, affected model/prompt, observed output,
root cause, and regression coverage here or in a focused investigation. Update the feature guide
only when the current usage contract changes.
