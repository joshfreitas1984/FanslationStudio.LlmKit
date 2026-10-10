## Frontier-model comparison

The comparison skill prepares a YAML `JudgeInput.yaml` from the completed assessment reports for
an external frontier-model harness. No judge model belongs in `Config.yaml` or in the ordinary
translation model list. The input includes all failures, structural issues, model disagreements,
and a deterministic sample of otherwise agreeing rows, with all candidate translations grouped by
`sampleId`.

The external harness returns YAML `Judgments.yaml` and may write an aggregated
`QualityComparison.yaml`. It should cache atomically and resume by sample or batch. Structural
validation remains deterministic; the frontier model is used for meaning, terminology, and
naturalness only.
# Translation model assessment

`TranslationAssessmentWorkflow.RunAsync` compares configured ordinary-translation models against
the same deterministic sample. It runs model phases sequentially so a local Ollama backend can
load one model at a time instead of alternating models per request.

The consuming project's `LlmConfig.TranslationAssessment` controls the run:

```yaml
translationAssessment:
  enabled: true
  modelNames:
    - Qwen25-Standard
    - HyMT2-7B
    - HyMT2-30B-A3B
  sampleSize: 500
  sampleSeed: 20260919
  fullCellSampleRatio: 0.5
  outputPath: TestResults/ModelAssessment
```

Results are written under the configured working directory and never modify `Converted`. Each
model has a `Results.yaml` file that is written atomically after every sample. Completed sample IDs
are skipped on restart, so a crashed model phase resumes from its first incomplete sample. A
completed model phase is skipped and the next configured model starts automatically. The sample
fingerprint, seed, and model name must match before existing results can be reused.

`Comparison.yaml` is written after all model phases finish. It reports elapsed time, average and
p95 sample latency, characters per second, estimated full-corpus time, failed samples, and the
  automated structural pass rate. Structural validity is not a translation-quality score. Each
  sample has a `sampleKind` of `split` or `fullCell`, plus optional `humanAccuracyScore` and
  `humanReviewNotes` fields for a consistent manual review pass over the same sample. Full-cell
  samples reconstruct compound templates from their original source fragments, matching the
  effective raw shape reviewed by the QC workflow.

## Gold-set mode (Assessments host)

`translationAssessment.source: goldSet` translates the QC gold set's own sources instead of sampling a
game's raw corpus, so no game is needed. `FanslationStudio.LlmKit.Assessments` configures it in
`Files/Config.yaml` (`goldSetPath`, `modelNames`, `outputPath: TestResults/ModelAssessment`) and runs it as
manual test "1b. Assess configured translation models (gold set)" with `LLMKIT_ASSESSMENTS=1`. Each gold
case is translated with its own glossary snapshot, every sample is `split`, and each result carries
`detectorFindings` (`LeftoverCjk`, `SelfReferenceLost`, `PromptLeak`). `sampleSeed` and `fullCellSampleRatio` do not
apply. The `compare-translation-models` skill reads this output.

## Regression gate and gold-set upkeep

Run the matching check before merging a change. Record the run in the commit message or a short entry under
`docs/investigations/`, not in auto-loaded files.

| If a change touches... | Run |
| --- | --- |
| a QC prompt or the QC engine | the QC evaluator on the full gold set; compare with the last archived `Comparison.yaml` |
| the translator prompt | the translation assessment (gold mode) and its `detectorFindings`; spot-read changed cases |
| a preset glossary entry | the preset-change impact and corpus audit scans across all games; the QC evaluator |
| a detector or validator | the detector blast-radius scan across all games (for example "6. Scan: prompt-leak detector") |
| the glossary engine | the overlap scans and the LlmKit unit suite |

- **Run-to-run noise.** The QC evaluator varies by about 1-2 items between identical runs. Compare with the archived
  run within that tolerance, not exactly.
- **Gold-set drift.** A case snapshots the glossary it was judged with. When a preset entry is later corrected,
  re-snapshot the affected cases deliberately and bump `labelVersion`, so a correct preset fix never shows up as a
  regression.
- **Cross-game prompt tuning.** One QC model and prompt serves every game. If a fix helps one game and hurts another,
  the gold set (with cases from both) is the arbiter; a game-specific prompt override is the rare fallback.
- **Detector coverage.** Detector findings only cover known defect shapes (`LeftoverCjk`, `SelfReferenceLost`,
  `PromptLeak`); they say nothing about meaning. Running QC detection over assessment outputs is an open option.
- **Sibling checkouts.** The scans read the games listed in `Files/Games.yaml`; a missing game is skipped with a
  warning, never a failure.
