using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Configuration;

/// <summary>
/// Config for the post-translation quality review pass (see docs/plans/quality-review-pass.md,
/// DragonHierOverLlm repo). Corresponds to a <c>qualityReview:</c> section in <c>Config.yaml</c>.
/// Defaults leave the whole feature a documented no-op (<see cref="Enabled"/> = false) for any
/// project/run that doesn't opt in - no packaging or workflow behavior changes unless a split
/// actually has a non-null <see cref="Support.TranslationSplit.QcQualityScore"/>, which only ever
/// happens after <see cref="Workflow.QualityReviewWorkflow"/> has run.
/// </summary>
public class QualityReviewConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Name of a model (matching a <see cref="ModelConfig.Name"/> entry under <c>models:</c>) to
    /// run the QC pass against - intentionally independent of the translation model(s), so QC can
    /// use a different (typically larger/more capable) model. Validated at config-load time in
    /// <see cref="ConfigurationExtensions.GetConfiguration"/> (throws if set but doesn't match a
    /// configured model), same as <see cref="LlmConfig.EscalationModelName"/>.
    /// </summary>
    public string? ModelName { get; set; }

    /// <summary>
    /// Max concurrent QC calls in flight. Falls back to <see cref="LlmConfig.MaxConcurrency"/>,
    /// then <see cref="LlmConfig.BatchSize"/>, then 20 - same fallback chain the translation
    /// schedulers already use. Note Ollama typically serves one request at a time per model
    /// regardless of this setting (see docs/translation-retry-escalation-and-fixes.md) - this
    /// caps in-flight requests, it does not guarantee proportional real throughput.
    /// </summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>
    /// 0-100 threshold (see <see cref="Support.TranslationSplit.QcQualityScore"/>) below which a
    /// reviewed split/column is treated as not-ready-to-package (same bucket as an
    /// unsafe/flagged/missing-translation split already falls into) AND flagged for human review
    /// (<see cref="Support.TranslationSplit.FlaggedForQcReview"/>). Only applies when
    /// <see cref="Support.TranslationSplit.QcQualityScore"/> is non-null (i.e. actually reviewed) -
    /// an unreviewed split is never held back by this check. 0 disables score-based
    /// gating/flagging entirely (a reviewed-but-never-corrected split still packages normally).
    /// Safe to change at any time and re-run packaging only - no LLM calls needed to see the
    /// effect, since the score is already stored per split.
    /// </summary>
    public int MinAcceptableScore { get; set; } = 70;

    /// <summary>
    /// How many times <see cref="Workflow.QualityReviewWorkflow.ApplyRulesToCurrentQcTranslated"/>
    /// will reset and retry the SAME underlying translation's QC correction before giving up on it
    /// (see <see cref="Support.TranslationSplit.QcRuleCheckFailureCount"/>) and surfacing it for a
    /// human instead. Unlike a normal translation attempt - where more resampling attempts keep
    /// helping, because there's real content variety to explore - a QC correction that still breaks
    /// the same rule after a few tries is usually a persistent false positive (e.g. a name that
    /// happens to match the bad-words list) that no amount of extra retries will fix, so this
    /// deliberately stays much lower than a translation retry budget. The safety net either way is
    /// the same: a column that's given up on still falls back to its last known-good
    /// <see cref="Support.TranslationSplit.Translated"/>, never a rule-breaking QcTranslated.
    /// </summary>
    public int MaxRuleCheckRetries { get; set; } = 3;

    /// <summary>
    /// How many extra "that broke the bad-words list, try again" turns
    /// <see cref="Workflow.QualityReviewWorkflow.GetLlmVerdictAsync"/> will spend in-line, within
    /// the same LLM call/cache entry, when a freshly proposed correction matches
    /// <see cref="Workflow.TranslationWorkflow.MatchesBadWords"/>, before giving up and handing the
    /// candidate back as-is for the normal accept/reject gate and <see cref="MaxRuleCheckRetries"/>-
    /// bounded cross-run retry to handle exactly as before. 0 (default) preserves the old
    /// single-shot behavior. Kept deliberately small and separate from
    /// <see cref="MaxRuleCheckRetries"/>, which bounds a much rarer cross-run "still stuck even
    /// after being told exactly what's wrong" case - this budget is spent inside one LLM round-trip
    /// session (a live conversation, not a fresh cold-started run), so it converges a stuck
    /// bad-words rejection in seconds instead of over several separate QC passes. See
    /// docs/quality-review-pass-architecture.md "Inline rule-check retries".
    /// </summary>
    public int InlineRuleCheckRetries { get; set; } = 0;

    /// <summary>
    /// Only meaningful when <see cref="TwoStageVerificationEnabled"/> is true. Bounds how many
    /// times <see cref="Workflow.QualityReviewWorkflow.GetCorrectionRepairAsync"/> ("call 3") will
    /// attempt to improve a correction that call 2 (<see cref="Workflow.QualityReviewWorkflow.GetVerificationVerdictAsync"/>)
    /// scored below <see cref="MinAcceptableScore"/>, re-scoring via call 2 after each attempt,
    /// before accepting whatever the last attempt was (still flagged via
    /// <see cref="Support.TranslationSplit.FlaggedForQcReview"/> if still below threshold - never
    /// discarded, same "accept once validated" philosophy every other QC retry follows). Default 2.
    /// </summary>
    public int MaxScoreRepairIterations { get; set; } = 2;

    /// <summary>
    /// DEFECT categories (see <see cref="QcDefectCategory"/>) a human has determined - by
    /// hand-validating a per-category sample from
    /// <see cref="Workflow.QualityReviewWorkflow.GetQcTriageAsync"/>'s <c>ByDefectCategory</c>
    /// output (written to <c>TestResults/QcTriageByDefectCategory.yaml</c> by
    /// <see cref="Workflow.QualityReviewWorkflow.WriteTriageReportAsync"/>) and computing that
    /// category's precision (genuine defects / sample size) - are low-precision enough (at or near
    /// 0%) that every flagged line in that category should be trusted/packaged wholesale despite
    /// its low <see cref="Support.TranslationSplit.QcQualityScore"/>, instead of held back for full
    /// human review like every other flagged line. See docs/qc-qualityscore-noise-investigation.md's
    /// "stratify by DEFECT category" policy step for the reasoning.
    ///
    /// Empty by default - no category is auto-accepted, so every flagged line keeps the old
    /// score-gated behavior (held back, <see cref="Support.TranslationSplit.FlaggedForQcReview"/>)
    /// until a category is explicitly added here. A category NOT listed here is unaffected
    /// regardless of what its eventual measured precision turns out to be - this is a deliberate
    /// per-category opt-in, not a default that could silently change behavior for a category nobody
    /// has actually hand-validated yet. Checked by
    /// <see cref="Utility.QualityReviewHelpers.PassesQcScoreGate"/>, the single choke point every
    /// packaging path uses for this decision - see its doc comment.
    /// </summary>
    public HashSet<QcDefectCategory> AutoAcceptDefectCategories { get; set; } = new();

    /// <summary>
    /// Controls the ENTIRE scoring/repair pipeline for any column the main QC call
    /// (<see cref="Workflow.QualityReviewWorkflow.GetLlmVerdictAsync"/>, "call 1") flags with a
    /// named DEFECT (anything but <see cref="QcDefectCategory.None"/>/<see cref="QcDefectCategory.Unknown"/>).
    /// Call 1 never self-scores its own draft (see docs/quality-review-pass-architecture.md
    /// postmortem #6 for why: a model grading its own freshly-authored text is a structural bias no
    /// rubric wording fixes) - scoring only ever happens in
    /// <see cref="Workflow.QualityReviewWorkflow.GetVerificationVerdictAsync"/> ("call 2"), which
    /// grades call 1's candidate (or a repaired one) without ever drafting text itself, and decides
    /// whether <see cref="Workflow.QualityReviewWorkflow.GetCorrectionRepairAsync"/> ("call 3") needs
    /// to attempt an improved fix (bounded by <see cref="MaxScoreRepairIterations"/>, re-scored by
    /// call 2 after every attempt).
    ///
    /// False by default. When false (or the model is missing either prompt), a column call 1
    /// corrects is accepted as-is (same validation gate either way) but gets NO score
    /// (<see cref="Support.TranslationSplit.QcQualityScore"/> stays null) and is always flagged via
    /// <see cref="Support.TranslationSplit.FlaggedForQcReview"/> - there is no fallback self-score to
    /// fall back to, since call 1 was never asked to produce one. A <see cref="QcDefectCategory.None"/>
    /// outcome (nothing to correct) is entirely unaffected by this flag either way - fixed score 100,
    /// no extra call, regardless of setting. Adds up to <c>MaxScoreRepairIterations + 1</c> calls to
    /// call 2 and up to <see cref="MaxScoreRepairIterations"/> calls to call 3, but only for the
    /// ~10-15% of a corpus call 1 flags with a named defect - never for a column call 1 passes.
    /// </summary>
    public bool TwoStageVerificationEnabled { get; set; } = false;

    /// <summary>
    /// Only meaningful when <see cref="TwoStageVerificationEnabled"/> is true. Runs call 2
    /// (<see cref="Workflow.QualityReviewWorkflow.GetVerificationVerdictAsync"/>) with Ollama's
    /// `think` mode on, instead of production's normal thinking-off default (see
    /// <see cref="Utility.LlmHelpers.GenerateLlmRequestData"/>). Call 2 is a narrow, single-claim
    /// judgment ("does call 1's claimed DEFECT actually hold up?") rather than an open-ended
    /// judgment, and only runs for the ~10-15% of a corpus call 1 already flagged - the call where
    /// reasoning is most likely to help without paying for it across the whole corpus. Never
    /// affects call 1 (<see cref="Workflow.QualityReviewWorkflow.GetLlmVerdictAsync"/>) or call 3
    /// (<see cref="Workflow.QualityReviewWorkflow.GetCorrectionRepairAsync"/>).
    ///
    /// Reasoning tokens are generated into the SAME num_predict/num_ctx budget as the final
    /// DEFECT:/SCORE: answer, so this deliberately does NOT swap in a bigger budget per-call
    /// (that would force Ollama to reload the model with different context params on every single
    /// verification call, since call 1/call 3 keep running against the same loaded model in
    /// between) - instead, the model's own <c>BaseFiles/&lt;Family&gt;/Config.yaml</c>
    /// <c>modelParams</c> need enough static headroom (e.g. Qwen38's num_ctx/num_predict were raised
    /// to 8192/4096) for a reasoning trace to fit before this flag is turned on, or a real reasoning
    /// trace can consume the whole budget before the model ever reaches SCORE:, turning a
    /// would-be-good verification into an unparseable/unscored one (see
    /// <see cref="Workflow.QualityReviewWorkflow.GetVerificationVerdictAsync"/>'s parse-failure
    /// branch) instead of an actual quality read. The reasoning trace itself is still always
    /// discarded before parsing (<see cref="TranslationService.TranslateMessagesAsync"/>'s
    /// `includeThinking` stays false) - only the final DEFECT:/SCORE: lines ever reach the regexes.
    ///
    /// False by default, matching every other production call's thinking-off default. Turn on to
    /// test whether it measurably improves verification precision (re-run the per-category
    /// hand-validation in docs/qc-qualityscore-noise-investigation.md before trusting it) - it costs
    /// extra latency/tokens per call, but only for the already-flagged subset.
    /// </summary>
    public bool VerificationThinkingEnabled { get; set; } = false;

    /// <summary>
    /// Only meaningful when <see cref="TwoStageVerificationEnabled"/> is true (and either verification
    /// prompt/repair prompt is present for the model). Sends a column call 1 (
    /// <see cref="Workflow.QualityReviewWorkflow.GetLlmVerdictAsync"/>) passed with <c>DEFECT: NONE</c>
    /// through call 2 (<see cref="Workflow.QualityReviewWorkflow.GetVerificationVerdictAsync"/>) too,
    /// as a genuine independent second opinion, instead of accepting call 1's NONE outright at a fixed
    /// score of 100 the way every other config does. Call 2 has no memory of call 1's own reasoning
    /// and never drafted the translation itself, so it isn't subject to the same self-consistency
    /// blind spot that let a genuine defect (e.g. a stitched-fragment seam glued directly to a markup
    /// tag with no natural connector) come back <c>DEFECT: NONE</c> from call 1 every time, even after
    /// the base prompt gained explicit wording for it - see
    /// docs/quality-review-pass-architecture.md postmortem #6 for why wording alone doesn't reliably
    /// close this kind of miss, and the tag-seam investigation this flag was added for.
    ///
    /// If call 2 also agrees nothing is wrong, the column is accepted exactly as before (fixed score
    /// 100, <see cref="QcDefectCategory.None"/>) - this flag costs nothing extra in that case beyond
    /// the one additional call. If call 2 instead names a real defect, call 2 itself never drafts
    /// text (see <see cref="Workflow.QualityReviewWorkflow.ScoreVerdict"/>'s doc comment), so call 3
    /// (<see cref="Workflow.QualityReviewWorkflow.GetCorrectionRepairAsync"/>) drafts the FIRST
    /// candidate for the confirmed defect (there is no prior attempt to improve on yet), and that
    /// candidate enters the SAME call-2 re-score/call-3 repair loop an ordinary call-1 correction
    /// does - it can be accepted as a real <see cref="QcStatus.Corrected"/> column exactly like any
    /// other. Only if the repair prompt is missing for this model, or call 3 can't draft anything at
    /// all, does the column fall back to a below-threshold-score retry (via the normal
    /// <see cref="Support.TranslationSplit.QcRuleCheckFailureCount"/>/<see cref="MaxRuleCheckRetries"/>
    /// budget) with no correction, permanently <see cref="Support.TranslationSplit.FlaggedForQcReview"/>
    /// once retries run out rather than force-corrected in code.
    ///
    /// False by default - doubles call 1's total LLM call count across the WHOLE corpus (every column,
    /// not just the ~10-15% call 1 already names a defect on), unlike every other
    /// <see cref="TwoStageVerificationEnabled"/> cost, which only applies to that flagged subset. Turn
    /// on only if that cost is acceptable for the recall improvement on missed defects.
    /// </summary>
    public bool VerifyNoDefectClaims { get; set; } = false;
}
