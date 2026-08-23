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
