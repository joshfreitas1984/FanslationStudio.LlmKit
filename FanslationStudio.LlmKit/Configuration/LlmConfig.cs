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
    public List<ModelConfig> Models { get; set; } = new();
    public GlossaryPresetConfig GlossaryPreset { get; set; } = new();

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
    /// side (see docs/OPTIMIZATION_PLAN.md in the FanslationStudio.LlmKit repo) so real translation
    /// runs can be compared before fully retiring the old path. Defaults to false (old behavior)
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
}

// Convert this further
// Change converted so we say on a split which model we'll target
// Instead of using StandardModel and StructuredTextModel, we have a dictionary<string, ModelExecutionConfig>
// When we pick up the split - using the dictionary key for the split
// Api Key would be Key<ApiKey>.txt
// Presets for Model params would have a structured/unstructured. - you can say whether you want structured or unstructured

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
}

public class ModelUrlConfig
{
    public string? ApiKey { get; set; }
    public bool? ApiKeyRequired { get; set; }
    public string? Url { get; set; }
    public string? Model { get; set; }

    public Dictionary<string, object>? ModelParams { get; set; }
}

public class ModelExecutionConfig : ModelUrlConfig
{
    public Dictionary<string, string> Prompts { get; set; } = [];
}
