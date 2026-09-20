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
    /// Bounds how many times call 5 (<see cref="Workflow.QualityReviewWorkflow.GetCorrectionRepairAsync"/>)
    /// will attempt to improve a correction call 4 (<see cref="Workflow.QualityReviewWorkflow.GetVerificationVerdictAsync"/>)
    /// found unresolved/regressed, re-verifying against the full confirmed defect set via call 4
    /// after each attempt, before accepting whatever the last attempt was (still flagged via
    /// <see cref="Support.TranslationSplit.FlaggedForQcReview"/> if still unresolved/below threshold -
    /// never discarded, same "accept once validated" philosophy every other QC retry follows).
    /// Default 2.
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
    /// Runs call 4 (<see cref="Workflow.QualityReviewWorkflow.GetVerificationVerdictAsync"/>) with
    /// Ollama's `think` mode on, instead of production's normal thinking-off default (see
    /// <see cref="Utility.LlmHelpers.GenerateLlmRequestData"/>). Call 4 only runs for the subset of a
    /// corpus calls 1/2 confirm at least one named defect on - the call where reasoning is most
    /// likely to help without paying for it across the whole corpus. Never affects calls 1/2
    /// (<see cref="Workflow.QualityReviewWorkflow.GetLlmVerdictAsync"/>), call 3
    /// (<see cref="Workflow.QualityReviewWorkflow.GetLlmVerdictAsync"/>'s correction generation), or
    /// call 5 (<see cref="Workflow.QualityReviewWorkflow.GetCorrectionRepairAsync"/>).
    ///
    /// Reasoning tokens are generated into the SAME num_predict/num_ctx budget as the final
    /// UNRESOLVED:/NEW_DEFECTS:/SCORE: answer, so this deliberately does NOT swap in a bigger budget
    /// per-call (that would force Ollama to reload the model with different context params on every
    /// single verification call, since the other calls keep running against the same loaded model in
    /// between) - instead, the model's own <c>BaseFiles/&lt;Family&gt;/Config.yaml</c>
    /// <c>modelParams</c> need enough static headroom (e.g. Qwen38's num_ctx/num_predict were raised
    /// to 8192/4096) for a reasoning trace to fit before this flag is turned on, or a real reasoning
    /// trace can consume the whole budget before the model ever reaches SCORE:, turning a
    /// would-be-good verification into an unparseable/unscored one (see
    /// <see cref="Workflow.QualityReviewWorkflow.GetVerificationVerdictAsync"/>'s parse-failure
    /// branch) instead of an actual quality read. The reasoning trace itself is still always
    /// discarded before parsing (<see cref="TranslationService.TranslateMessagesAsync"/>'s
    /// `includeThinking` stays false) - only the final labeled lines ever reach the regexes.
    ///
    /// False by default, matching every other production call's thinking-off default. Turn on to
    /// test whether it measurably improves verification precision (re-run the per-category
    /// hand-validation in docs/qc-qualityscore-noise-investigation.md before trusting it) - it costs
    /// extra latency/tokens per call, but only for the already-flagged subset.
    /// </summary>
    public bool VerificationThinkingEnabled { get; set; } = false;

    /// <summary>
    /// Runs detection (calls 1/2, <see cref="Workflow.QualityReviewWorkflow.DetectDefectsAsync"/>)
    /// twice and merges via <see cref="Support.QcDetectionResult.Merge"/> (a set union - doubling can
    /// only match or exceed a single call's catch rate), instead of trusting call 1 alone. Measured
    /// in docs/investigations/tests/qc-evaluator-model-selection.md: a real but thin recall lift
    /// (1 of 18 in-scope defect rows caught only by the merge) at slightly more than double the
    /// detection-phase latency (measured directly against the current production quant: 1268ms vs
    /// 601ms per row) - roughly 15 hours across the full corpus's ~81,000 splits. Kept true
    /// (production-matching) as the default; set false to trade that thin recall lift back for
    /// detection-phase throughput when speed is the priority.
    /// </summary>
    public bool DoubledDetectionEnabled { get; set; } = true;
}
