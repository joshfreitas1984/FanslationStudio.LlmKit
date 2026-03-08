namespace FanslationStudio.LlmKit.Configuration;

public class ModelConfig: ModelUrlConfig
{
    public string Name { get; set; } = string.Empty;
    public ModelPreset ModelPreset { get; set; } = ModelPreset.None;
    public ModelPresetType ModelPresetType { get; set; } = ModelPresetType.Standard;

    public string CustomPromptsPath { get; set; } = string.Empty;
    public string ApiKeyFilePath { get; set; } = string.Empty;
}

public enum ModelPresetType
{
    Standard,
    StructuredText,
}

public enum ModelPreset
{
    None,
    Qwen25,
    //Qwen35,
}

public class PresetConfig
{
    public bool? ApiKeyRequired { get; set; }
    public string? Url { get; set; }
    public string? Model { get; set; }
    public Dictionary<string, object>? ModelParams { get; set; }
    public Dictionary<string, object>? StructuredTextModelParams { get; set; }
}