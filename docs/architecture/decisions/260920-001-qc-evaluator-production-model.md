# ADR 260920-001: Select Qwen38Qc-IQ4XS as the Production QC Evaluator Model

- Status: Accepted
- Date: 2026-09-20

## Context

The five-call quality review process (`QualityReviewWorkflow`) needs one production model/quant for
detection and verification. Several `qwen3.8-27b` QC-tuned quants were compared against a
human-labeled gold set (`DragonHierOverLlm/Files/Goldset/GoldSet.yaml`, 108 detection items) via
`QualityEvaluatorAssessmentWorkflow`, after the shared `BaseQualityReviewPrompt.txt` was iterated to
a point where every gold-set category cleared a comfortable recall bar. A prompt change invalidates
any standing quant comparison, so the final sweep was run once the prompt stabilized.

## Decision

Use `Qwen38Qc-IQ4XS` (`UD-IQ4_XS` quant) as the production QC evaluator model. On the full
108-entry gold set it strictly beat the prior default `Qwen38Qc` (`UD-Q3_K_XL`) on both recall
(0.855 vs 0.812) and precision (0.894 vs 0.875), at a real but bounded latency cost (roughly +36%
estimated full-corpus runtime). A third candidate, `Qwen38Qc-UDQ4KM` (`UD-Q4_K_M`), scored higher
recall still (0.884) but lower precision than IQ4XS and a much larger latency cost (roughly +111%
over the old default) - rejected as the wrong tradeoff for the recall gained.

Full methodology, per-category breakdown, and the excluded-methodology recall/precision
reconstruction (`Scripts/qc_excluded_methodology.py`) live in the downstream `DragonHierOverLlm`
repo's `docs/plans/qc-evaluator-comparison.md` ("Current State" summary and the Twenty-first round
entry). This ADR records the decision and rationale for `FanslationStudio.LlmKit`, which owns the
model configs and prompts but not the gold set or the assessment run history.

## Consequences

`qualityReview.modelName` in downstream `Config.yaml` files should point at `Qwen38Qc-IQ4XS` for
production QC. Full cold-start corpus QC runs take roughly 3.6 days at this quant on the reference
hardware (16.3GB VRAM), up from roughly 2.7 days at the old default - a real cost that should be
accounted for when scheduling a full-corpus QC pass. Revisit this choice if the shared
`BaseQualityReviewPrompt.txt` changes materially, since a prompt change can shift the
recall/precision/latency balance between quants (this happened once already, see the Ninth vs.
Twenty-first round entries in the plan doc above).
