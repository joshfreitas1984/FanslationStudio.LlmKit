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

