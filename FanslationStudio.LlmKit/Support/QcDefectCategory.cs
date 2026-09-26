namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// The QC prompt's <c>DEFECT:</c> category (see <c>BaseQualityReviewPrompt.txt</c>, all model
/// families) - the model must name one of these before scoring, so a flagged
/// <see cref="TranslationSplit.QcQualityScore"/> comes with a reason instead of just a number. See
/// docs/qc-qualityscore-noise-investigation.md (Tests project, DragonHierOverLlm repo) for why this
/// was added and how it's meant to be used for per-category triage.
/// </summary>
public enum QcDefectCategory
{
    /// <summary>No <c>DEFECT:</c> line was parsed from the model's response - either this split
    /// predates the DEFECT-first prompt (old serialized data), or the response was otherwise
    /// unparseable. Distinct from <see cref="None"/> (the model explicitly said nothing is
    /// wrong).</summary>
    Unknown = 0,

    /// <summary>Model said <c>DEFECT: NONE</c> - nothing wrong found.</summary>
    None,

    /// <summary>A garbled/dropped/duplicated number or clause.</summary>
    GarbledNumber,

    /// <summary>A mistranslated domain term that breaks the setting.</summary>
    DomainTerm,

    /// <summary>A lost idiom/slang meaning rendered as a literal word-for-word gloss.</summary>
    LostIdiom,

    /// <summary>A term left untranslated/transliterated as Pinyin when it has a clear translatable
    /// meaning.</summary>
    UntranslatedPinyin,

    /// <summary>An omitted subject/object/clause, or a dropped title/honorific next to a
    /// placeholder.</summary>
    DroppedContent,

    /// <summary>A SOURCE stammer/stutter (repeated leading syllable(s) before a word) collapsed
    /// into a single unstammered word, or expanded into a full separately-spoken repeated word
    /// instead of a hyphenated partial repeat.</summary>
    DroppedStutter,

    /// <summary>A stitched-fragment seam that is genuinely hard to parse, ambiguous, or changes
    /// meaning.</summary>
    HardToParseSeam,

    /// <summary>Something else concrete and nameable, not covered by the categories above.</summary>
    OtherNamedDefect,

    /// <summary>SOURCE's clause-level meaning is inverted rather than merely mistranslated at the
    /// word/phrase level - who is doing/granting/asking what to/of whom is flipped (e.g. "I'll
    /// spare you" rendered as "spare me", a threat rendered as a plea, blame rendered as an
    /// admission). Distinct from <see cref="DroppedContent"/> (nothing is omitted - every word has
    /// a rendering, the relationship between them is backwards) and from <see
    /// cref="OtherNamedDefect"/> (this is a specific, nameable failure shape, not a residual
    /// catch-all). Deliberately left off <see
    /// cref="Configuration.QualityReviewConfig.AutoAcceptDefectCategories"/> by default - unlike
    /// <see cref="DroppedContent"/>/<see cref="OtherNamedDefect"/>, this shape has no
    /// hand-validated low-severity track record yet, and ships a translation whose surface reads
    /// fluently while contradicting SOURCE, the highest-consequence kind of miss. See
    /// docs/qc-qualityscore-noise-investigation.md (Tests project, DragonHierOverLlm repo) for the
    /// case that added this category.</summary>
    MeaningReversal,

    /// <summary>Model believes something about the translation may be off but isn't confident
    /// enough to name a specific category or draft a fix it trusts - see
    /// docs/quality-review-pass-architecture.md postmortem #9. Unlike every other non-<see
    /// cref="None"/> category, this one is never paired with a real <c>CORRECTED</c> fix (call 1's
    /// drafted text, if any, is discarded - see <c>QualityReviewWorkflow.GetLlmVerdictAsync</c>),
    /// never runs two-stage verification (there is nothing to grade), is never eligible for
    /// <see cref="Configuration.QualityReviewConfig.AutoAcceptDefectCategories"/> (there is no
    /// <see cref="TranslationSplit.QcTranslated"/> to accept), and always leaves the column flagged
    /// with no score - a genuine "ask a human" signal instead of forcing a low-confidence hunch to
    /// round up to a fully-committed named defect and fix.</summary>
    Uncertain,
}
