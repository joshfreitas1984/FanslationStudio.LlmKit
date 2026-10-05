namespace FanslationStudio.LlmKit.Support;

/// <summary>
/// Per-line context a game supplies through <see cref="Configuration.GameHooks.LineContextProvider"/>: a short
/// prompt added to the translator's system prompt (who is speaking, whether this is narration), plus whether the
/// speaker's gender is actually known. When it is, a he/she in the translation is correct and the soft
/// invented-gender correction (see <see cref="ValidationResult.SoftCorrectionPrompt"/>) is skipped.
/// <see cref="Gender"/> is <see cref="Male"/> or <see cref="Female"/> when known, so existing translations can be
/// checked for a pronoun that contradicts it (see <see cref="LineValidation.ContradictsGender"/>). It is empty when the
/// gender is known but there is no single one to check against (a line naming characters of both genders).
/// </summary>
public sealed record LineContext(string Prompt, bool GenderKnown, string Gender = "")
{
    public const string Male = "male";
    public const string Female = "female";
}
