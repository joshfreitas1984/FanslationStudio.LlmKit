namespace FanslationStudio.LlmKit.Configuration;

public class GlossaryPresetConfig
{
    /// <summary>
    /// Presets for Wuxia Games
    /// </summary>
    public bool UsePresetChineseGlossary { get; set; } = true;
    public List<ChineseGlossaryTypes> ChineseGlossaryTypesToSupress { get; set; } = [];
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