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
}
