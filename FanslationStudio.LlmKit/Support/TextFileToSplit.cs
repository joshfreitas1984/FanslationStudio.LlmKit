using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Support;

public enum TextFileType
{
    /// <summary>
    /// Genuine CSV game data - comma-delimited, quote-aware (parsed/rebuilt via
    /// <see cref="Utility.CompoundFieldSplitter.ParseCsvRow"/>/<see cref="Utility.CompoundFieldSplitter.RebuildCsvRow"/>),
    /// one or more columns per row, handled by <see cref="Workflow.CsvGameDataWorkflow"/>. Named
    /// explicitly (previously "RegularDb", the implicit/default type that every consuming project
    /// happened to treat as CSV) so this stays a deliberate, narrow assumption instead of an
    /// implied one - a game whose "main" data file is TSV/JSON/a custom text format is not this
    /// type and needs its own.
    /// </summary>
    RawCsv,
    PrefabText,
    DynamicStrings,
    /// <summary>
    /// Hardcoded, runtime-assembled string literals baked directly into IL2CPP game code (e.g. a
    /// String.Concat/String.Format call mixing Chinese literal fragments with data), discovered
    /// via the consuming project's decompiled-code inspection (see DragonHeirOverLlm's
    /// Converter output + "dynamic/hardcoded in-code string translation plan"). Distinct from the
    /// older <see cref="DynamicStrings"/> value, which targeted a Mono/Cecil-transpiler based
    /// dump+patch approach that does not work against IL2CPP (dummy assemblies have no real IL
    /// bodies to transpile) - this value is for the newer, IL2CPP-safe Harmony-postfix +
    /// exact-substring-replace approach instead (see DynamicStringWorkflow).
    /// </summary>
    DynamicStringsIL2CPP,
    LocalTextString
}

public class TextFileToSplit
{
    [YamlMember(ScalarStyle = ScalarStyle.DoubleQuoted)]
    public string Path { get; set; } = string.Empty;

    public bool PackageOutput { get; set; } = true;

    public TextFileType TextFileType { get; set; } = TextFileType.RawCsv;

    public bool IsMainDialogueAsset { get; set; } = false;

    public bool EnableGlossary { get; set; } = true;

    /// <summary>
    /// When false, <see cref="Workflow.QualityReviewWorkflow"/> never reviews any column in this
    /// file - no LLM calls, no Qc* fields ever get set, and packaging always serves plain
    /// <see cref="TranslationSplit.Translated"/> for it. For content where the QC model's
    /// glossary/naturalness-oriented review doesn't apply or would do more harm than good - e.g. a
    /// dedicated name-fragment file consumed only by an exact-match substring dictionary
    /// (heroNameParts.txt/forceNameParts.txt-style files), or free-verse/quote text where a "more
    /// natural" QC rewrite is actually less faithful to the source.
    /// </summary>
    public bool EnableQualityReview { get; set; } = true;

    public string AdditionalPromptName { get; set; } = string.Empty;

    public bool EnableBasePrompts { get; set; } = true;

    public bool RemoveNumbers { get; set; } = false;

    public bool RemoveExtraFullStop { get; set; } = true;

    public bool RemoveExtraThe { get; set; } = true;

    public bool IgnoreGameObjects { get; set; } = false;

    public bool NameCleanupRoutines { get; set; } = false;

    public bool NameCleanupRoutines2 { get; set; } = false;

    public bool AllowMissingColorTags { get; set; } = true;

    public bool IgnoreHtmlTagsInText { get; set; } = false;

    /// <summary>
    /// Zero-based CSV column indices that should never be decomposed/translated for this file -
    /// e.g. an icon/resource-path column that happens to contain CJK characters but is not
    /// user-facing text. Skipped columns are left completely untouched (no
    /// <see cref="TranslationSplit"/> or <see cref="FieldTemplate"/> is created for them at all),
    /// so it doesn't matter whether the column's content would otherwise have decomposed into one
    /// fragment or several sub-fragments - the column is never passed to
    /// <c>CompoundFieldSplitter.Decompose</c> in the first place and is reconstructed verbatim
    /// from the original raw CSV.
    /// </summary>
    public HashSet<int> SkipColumns { get; set; } = [];
}
