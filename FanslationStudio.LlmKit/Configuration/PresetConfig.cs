namespace FanslationStudio.LlmKit.Configuration;

/// <summary>
/// Presets from the platform making it easy to get stuff up and running.
/// Values will be overriden with local files.
/// </summary>
public class PresetConfig
{
    // Presets for Wuxia Games
    public bool UsePresetChineseGlossary { get; set; } = true;
    public List<ChineseGlossaryTypes> ChineseGlossaryTypesToSupress { get; set; } = [];

    public ModelPreset StandardModelPreset { get; set; } = ModelPreset.None;

    public ModelPreset StructuredTextModelPreset { get; set; } = ModelPreset.None;
}

public enum ModelPreset
{
    None,
    Qwen25,
    //Qwen35,
}

public enum ChineseGlossaryTypes
{
    CommonStats,
    GameTerms,
    Idioms,
    ItemsAndMinerals,
    OtherGlossary,
    Phonetics,
    Places,
    Sects,
    Time,
    Titles,
    Weapons,
    WuxiaTerms,
    XianxiaTerms,
}

public class PresetModelConfig
{
    public bool? ApiKeyRequired { get; set; }
    public string? Url { get; set; }
    public string? Model { get; set; }
    public Dictionary<string, object>? ModelParams { get; set; }
    public Dictionary<string, object>? StructuredTextModelParams { get; set; }
}