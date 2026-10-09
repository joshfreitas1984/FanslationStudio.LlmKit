using FanslationStudio.LlmKit.Support;
using System.Collections.Concurrent;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Configuration;


public class LlmConfig
{
    ///// <summary>
    ///// Model to use for translating simple text that doesn't have placeholders, html or other complex structures.
    ///// </summary>
    //public ModelUrlConfig? WorkspaceStandardModel { get; set; }

    ///// <summary>
    ///// Model to use for translating text that have placeholders, html and other complex structures.
    ///// </summary>
    //public ModelUrlConfig? WorkspaceStructuredTextModel { get; set; }

    public int? RetryCount { get; set; }
    public int? BatchSize { get; set; }
    public bool SkipLineValidation { get; set; }
    public bool CorrectionPromptsEnabled { get; set; }
    public bool TranslateFlagged { get; set; }

    /// <summary>
    /// Off by default. When true, <see cref="GameHooks.LineContextProvider"/> (if the game set one) supplies a
    /// per-line context - e.g. the speaker and their gender - that is added to that line's system prompt.
    /// </summary>
    public bool LineContextEnabled { get; set; }

    /// <summary>
    /// The pronoun check run by the rules pass (<see cref="Workflow.TranslationWorkflow.ApplyAllRulesToCurrentTranslation"/>)
    /// and the default for <see cref="Workflow.PronounDefectWorkflow"/>. On by default; see <see cref="PronounCheckConfig"/>.
    /// </summary>
    public PronounCheckConfig PronounCheck { get; set; } = new();
    public List<ModelConfig> Models { get; set; } = new();
    public GlossaryPresetConfig GlossaryPreset { get; set; } = new();

    /// <summary>
    /// Post-translation quality control pass config (see docs/plans/quality-control-pass.md in
    /// DragonHierOverLlm). Optional - defaults to <see cref="QualityControlConfig.Enabled"/> =
    /// false, a documented no-op for any project that doesn't opt in.
    /// </summary>
    public QualityControlConfig QualityControl { get; set; } = new();

    /// <summary>
    /// Legacy <c>qualityReview:</c> config key, read for one release and mapped onto
    /// <see cref="QualityControl"/> by <see cref="ConfigurationExtensions.GetConfiguration"/>.
    /// </summary>
    [YamlMember(Alias = "qualityReview")]
    public QualityControlConfig? LegacyQualityReview { get; set; }

    public TranslationAssessmentConfig TranslationAssessment { get; set; } = new();

    public QualityControlAssessmentConfig QualityControlAssessment { get; set; } = new();

    /// <summary>
    /// Name of a model (matching a <see cref="ModelConfig.Name"/> entry in <see cref="Models"/>) to
    /// escalate a split to once it has exhausted its normal <see cref="RetryCount"/> budget against
    /// its originally assigned model and is still invalid. Optional - if null/empty (or resolves to
    /// the very same model the split already used), escalation is a no-op and behavior is
    /// unchanged from before this feature existed. Validated against <see cref="Runtime"/>.Models
    /// at config-load time in <see cref="ConfigurationExtensions.GetConfiguration"/> so a typo'd
    /// name fails fast instead of silently never escalating.
    /// </summary>
    public string? EscalationModelName { get; set; }

    /// <summary>
    /// Whole-cell retry attempts to spend against <see cref="EscalationModelName"/> (mirrors
    /// <see cref="RetryCount"/> but scoped to the escalation model only). Defaults to 1 if
    /// <see cref="EscalationModelName"/> is set but this isn't.
    /// </summary>
    public int? EscalationRetryCount { get; set; }

    /// <summary>
    /// When true, <see cref="TranslationService.TranslateViaLlmAsync"/> uses the continuous
    /// worker-pool scheduler (<see cref="TranslationService.TranslateViaLlmAsyncPooled"/>) instead
    /// of the original sequential-batch scheduler
    /// (<see cref="TranslationService.TranslateViaLlmAsyncBatched"/>). The pooled scheduler removes
    /// the "wait for the whole batch of <see cref="BatchSize"/> to finish before starting the next
    /// one" barrier and the "translate one file fully before starting the next" barrier - workers
    /// pull the next unique string to translate as soon as they finish one, across every file in
    /// this run, bounded only by <see cref="MaxConcurrency"/>. Both schedulers are kept side by
    /// side so existing projects can compare runs before changing their scheduler choice.
    /// Defaults to false (old behavior)
    /// until validated on a real run.
    /// </summary>
    public bool UseContinuousWorkerPool { get; set; }

    /// <summary>
    /// Maximum number of translation requests the continuous worker pool
    /// (<see cref="UseContinuousWorkerPool"/>) will have in flight at once, across all files in
    /// this run. This is the real concurrency knob for the pooled scheduler - unlike
    /// <see cref="BatchSize"/> in the old scheduler, it is not also a checkpoint/flush boundary.
    /// Falls back to <see cref="BatchSize"/> (then 20) if not set, so existing configs work
    /// unchanged when opting into the pooled scheduler.
    /// </summary>
    public int? MaxConcurrency { get; set; }

    public List<string> SplitRegexPatterns { get; set; } = new();
    public List<string> SplitCharactersList { get; set; } = new();
    public List<string> ExtraStringTokenReplacers { get; set; } = new();

    [YamlIgnore]
    public RuntimeValues Runtime { get; set; } = new();

    /// <summary>
    /// Optional, game-specific extension points for the pipeline - see <see cref="GameHooks"/>.
    /// Set via <see cref="ConfigurationExtensions.GetConfiguration"/>'s optional parameter (or a
    /// top-level workflow entry point's, which forwards it there); never populated from YAML.
    /// </summary>
    [YamlIgnore]
    public GameHooks Hooks { get; set; } = new();
}

public class TranslationAssessmentConfig
{
    public bool Enabled { get; set; }
    public List<string> ModelNames { get; set; } = [];
    public int SampleSize { get; set; } = 500;
    public int SampleSeed { get; set; } = 20260919;
    public double FullCellSampleRatio { get; set; } = 0.5;
    public string OutputPath { get; set; } = "TestResults/ModelAssessment";

    /// <summary>
    /// Exact source cell/split text (matched verbatim against the Raw/Export candidates built by
    /// <see cref="TranslationAssessmentWorkflow"/>) always included in the sample regardless of
    /// <see cref="SampleSeed"/>/<see cref="SampleSize"/> random selection - e.g. known regression
    /// cases worth tracking on every run. Added on top of <see cref="SampleSize"/>, not counted
    /// against it. A pinned entry with no matching candidate in the current corpus is skipped with
    /// a console warning rather than failing the run.
    /// </summary>
    public List<string> PinnedSampleSources { get; set; } = [];
}

public class QualityControlAssessmentConfig
{
    public bool Enabled { get; set; }
    public List<string> ModelNames { get; set; } = [];
    public string GoldSetPath { get; set; } = "TestResults/QcAssessment/GoldSet.yaml";
    public string OutputPath { get; set; } = "TestResults/QcAssessment";

    /// <summary>
    /// Whether detection runs both calls 1 and 2 (production's default) and merges them via
    /// <see cref="Support.QcDetectionResult.Merge"/>, or only call 1 alone. See
    /// docs/plans/qc-evaluator-comparison.md's "Process Variants" section - this measures the
    /// recall/latency tradeoff of doubled detection independent of a specific model/quant.
    /// </summary>
    public bool DoubledDetection { get; set; } = true;

    /// <summary>
    /// Runs detection (calls 1/2, <see cref="Workflow.QualityControlWorkflow.DetectDefectsAsync"/>)
    /// with Ollama's `think` mode on instead of production's normal thinking-off default. Mirrors
    /// <see cref="QualityControlConfig.VerificationThinkingEnabled"/> but for the detection role
    /// instead of verification - a scoped, assessment-only way to test whether a candidate
    /// detector's capability gap on hard semantic categories (name-as-gloss, invented-synonym-pair)
    /// is a reasoning-budget problem rather than a genuine ceiling, without touching production's
    /// <see cref="Workflow.QualityControlWorkflow.RunAsync"/>/<see cref="Workflow.QualityControlWorkflow.RunBruteForce"/>
    /// path (neither ever passes this flag - see docs/investigations/tests/qc-evaluator-model-selection.md).
    /// As with verification thinking, the reasoning trace shares the same num_ctx/num_predict budget
    /// as the DEFECTS output line, so a candidate model's `modelParams` may need headroom (see that
    /// flag's doc comment) before this is worth turning on against it. Default false preserves
    /// existing detection behavior.
    /// </summary>
    public bool DetectionThinkingEnabled { get; set; }

    /// <summary>
    /// Whether correction verification (call 4) runs twice (fresh, independent calls merged via
    /// <see cref="Support.QcVerificationResult.Merge"/>, strictly - either call's objection rejects
    /// the correction) or once. See docs/plans/qc-evaluator-comparison.md's "Process Variants"
    /// section, Twelfth round: measured to make no difference against the two harmful-correction
    /// gold examples known at the time (both calls made the identical mistake) - kept as a harness
    /// knob for re-testing against a larger harmful-correction denominator, not because it's
    /// currently believed to help.
    /// </summary>
    public bool DoubledVerification { get; set; } = true;

    /// <summary>
    /// Candidate models to test in the CORRECTION-GENERATION (call 3) role, instead of always using
    /// the detector model - each drafts a correction for every gold row with known confirmed defect
    /// categories, then <see cref="JudgeModelName"/> scores every draft's safety via
    /// <see cref="Workflow.QualityControlWorkflow.GetVerificationVerdictAsync"/>. A model never grades
    /// its own draft. See docs/plans/qc-fast-corrector-model-swap.md - this is that plan's validation
    /// step. Empty (default) skips this evaluation mode entirely; existing detection/verification-only
    /// rounds are unaffected.
    /// </summary>
    public List<string> CorrectorModelNames { get; set; } = [];

    /// <summary>
    /// Trusted judge model used to score every <see cref="CorrectorModelNames"/> entry's drafted
    /// corrections for safety. Must be configured with a BaseQualityControlVerificationPrompt and must
    /// not appear in <see cref="CorrectorModelNames"/>. Required only when CorrectorModelNames is
    /// non-empty.
    /// </summary>
    public string? JudgeModelName { get; set; }

    /// <summary>
    /// When true, the correction-generation comparison mirrors production's full verify/repair loop
    /// (<see cref="Workflow.QualityControlWorkflow.GetLlmVerdictAsync"/>'s calls 4/5) instead of a
    /// single verify-only pass: a rejected draft goes back through the SAME corrector model's
    /// <see cref="Workflow.QualityControlWorkflow.GetCorrectionRepairAsync"/>, then <see cref="JudgeModelName"/>
    /// re-verifies, up to <see cref="QualityControlConfig.MaxScoreRepairIterations"/> repairs per row.
    /// Like production, a draft the validation gate rejects is repaired against the gate's reason before
    /// any verify call (<see cref="QualityControlConfig.PreVerificationGateEnabled"/>), judge evidence
    /// reaches the repair (<see cref="QualityControlConfig.VerificationEvidenceEnabled"/>), and an
    /// unchanged repair stops the row. Answering
    /// whether a cheap corrector's higher initial failure rate is rescued by repair, not just how
    /// often its first draft succeeds. Written to a separate `CorrectionGenerationWithRepair` output
    /// directory so single-shot and repair-loop numbers are never conflated. Default false preserves
    /// the existing single-shot-only behavior.
    /// </summary>
    public bool EnableRepairLoop { get; set; }
}

// Convert this further
// Change converted so we say on a split which model we'll target
// Instead of using StandardModel and StructuredTextModel, we have a dictionary<string, ModelExecutionConfig>
// When we pick up the split - using the dictionary key for the split
// Api Key would be Key<ApiKey>.txt
// Presets for Model params would have a structured/unstructured. - you can say whether you want structured or unstructured

/// <summary>Config.yaml's <c>pronounCheck</c> section.</summary>
public class PronounCheckConfig
{
    /// <summary>
    /// Flag translations with an invented or wrong gender, or subject-less narration written as "I", for
    /// retranslation during the rules pass. A line the model cannot fix is flagged every time, so brute force
    /// keeps retranslating it (at most 30 iterations): that is the cue to add it to the gold set and tune the
    /// prompt. Turn it off if it keeps flagging lines you cannot fix.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Leave out an invented-gender hit whose translation names a character. Set this for a game whose text is
    /// running prose, where a pronoun after a named character is usually right (see
    /// <see cref="Workflow.PronounDefectWorkflow.PronounDefectOptions.Prose"/>).
    /// </summary>
    public bool SkipWhenTranslationNamesSomeone { get; set; }

    /// <summary>
    /// Off by default; a game opts in with <c>autoRepairPronouns: true</c>. When the model still writes an invented he/she/his/her/him after the retries and the soft correction, rewrite it
    /// to they/their/them (fixing the verb) instead of shipping it. Only a rewrite the verb rules can do safely is used
    /// (see <see cref="PronounRepair"/>). It is the deterministic end of "never guess a gender": a line the model cannot
    /// fix does not stay stuck, and a 14B model that ignores the instruction cannot keep a guessed gender in.
    /// </summary>
    public bool AutoRepairPronouns { get; set; }
}

public class RuntimeValues
{
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, ModelExecutionConfig> Models { get; set; } = new();
    public List<GlossaryLine> GlossaryLines { get; set; } = [];
    public List<GlossaryLine> ManualTranslations { get; set; } = [];

    // ConcurrentDictionary because this is read and written from the parallel translation workers
    // in TranslationService.TranslateViaLlmAsync (a plain Dictionary is not thread-safe for
    // concurrent reads/writes and could corrupt its internal state or throw under contention).
    public ConcurrentDictionary<string, string> TranslationCache { get; set; } = new();

    /// <summary>
    /// Index of every "only"/"exclude"-restricted <see cref="GlossaryLines"/>/
    /// <see cref="ManualTranslations"/> entry, keyed by each of its Raw/RawSimplified/
    /// RawTraditional variants, built once per run by
    /// <see cref="TranslationService.FillTranslationCacheAsync"/>. Exists so
    /// <see cref="TranslationService"/> can check "is this split's text file-restricted?" and
    /// "does this split have a direct 'only' override for this file?" in O(1) per split instead of
    /// re-scanning the (potentially large) glossary/manual lists with LINQ on every single split,
    /// across every parallel worker, for the lifetime of the run.
    /// </summary>
    public Dictionary<string, List<GlossaryLine>> FileRestrictedEntriesByText { get; set; } = new();

    /// <summary>
    /// Per-split context from <see cref="GameHooks.LineContextProvider"/>, built once per run before any
    /// translation starts (keyed by split instance - <see cref="TranslationSplit"/> has no value equality).
    /// Empty unless <see cref="LlmConfig.LineContextEnabled"/>.
    /// </summary>
    public ConcurrentDictionary<TranslationSplit, LineContext> LineContexts { get; set; } = new();
}

public class ModelUrlConfig
{
    public string? ApiKey { get; set; }
    public bool? ApiKeyRequired { get; set; }
    public bool? EnableThinking { get; set; }
    public string? Url { get; set; }
    public string? Model { get; set; }

    public Dictionary<string, object>? ModelParams { get; set; }
}

public class ModelExecutionConfig : ModelUrlConfig
{
    public Dictionary<string, string> Prompts { get; set; } = [];
}
