using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Configuration;

/// <summary>
/// Optional, game-specific extension points for the translation/validation pipeline. Set once by
/// the consuming project (e.g. passed into <see cref="ConfigurationExtensions.GetConfiguration"/>
/// or a top-level workflow entry point) and carried on <see cref="LlmConfig.Hooks"/> from there -
/// every call site that needs a hook already has the <see cref="LlmConfig"/> in scope, so this
/// travels for free instead of needing its own parameter threaded everywhere.
///
/// Previously these were static, mutable properties on <c>LineValidation</c>, set as a side effect
/// of a consuming project's static constructor running (see DragonHierOverLlm's
/// <c>GameFileHandling</c> history) - fragile, because a static constructor only runs the first time
/// something on that type is actually touched, so a test/entry point whose only reference was a
/// <c>static readonly</c> field (not a method) could silently run with hooks never registered. An
/// explicit object passed through <see cref="LlmConfig"/> removes that class of bug entirely.
/// </summary>
public class GameHooks
{
    /// <summary>
    /// Invoked at the very end of <see cref="LineValidation.PrepareResult"/> - i.e. immediately
    /// after every LLM call (each attempt in the main retry loop, and each round of
    /// <see cref="TranslationService.CorrectSentenceBySentenceAsync"/>) and before
    /// <see cref="LineValidation.CheckTransalationSuccessful"/> gets a chance to validate/flag the
    /// result. Lets a game-specific project deterministically fix up known LLM quirks - like a
    /// possessive/contraction suffix ending up glued inside a placeholder token, e.g.
    /// "#PlayerName's#" instead of "#PlayerName#'s" - instead of paying for a whole retry
    /// round-trip to fix something a plain string replace already knows how to repair. Receives
    /// (raw, llmResult) and must return the (possibly repaired) result. Left null (no-op) unless a
    /// caller opts in.
    /// </summary>
    public Func<string, string, string>? CustomPostRepair { get; set; }

    /// <summary>
    /// Invoked at the very end of <see cref="LineValidation.CheckTransalationSuccessful"/>, only
    /// when every built-in check above has already passed. Lets a game-specific project add
    /// validation rules that only make sense for one specific column of one specific file - e.g. a
    /// compound field ('|'-separated choice options, each further ';'-separated by
    /// <see cref="Utility.CompoundFieldSplitter"/> into literal template text) where an LLM
    /// bleeding a stray '|' into a translated fragment would silently desync the game's own
    /// indexing - but a plain "'|' appeared in the translation" rule would be wrong to apply
    /// file-wide/globally, since other columns (or other games entirely) may have '|' appear
    /// legitimately in natural translated text.
    /// Receives (textFile, column, raw, result) - column is the zero-based CSV column index the
    /// fragment came from when known (see <see cref="Support.TranslationSplit.Split"/>), or null
    /// when validation is running outside a column context. Return null when the hook finds
    /// nothing wrong; return a non-null correction-prompt-style reason string to flag the result as
    /// invalid and feed that reason back into the retry/correction loop, same as the built-in
    /// checks above. Left null (no-op) unless a caller opts in.
    /// </summary>
    public Func<TextFileToSplit, int?, string, string, string?>? CustomColumnValidator { get; set; }

    /// <summary>
    /// Invoked at the end of <see cref="LineValidation.PrepareResult"/>, before
    /// <see cref="CustomColumnValidator"/>/<see cref="LineValidation.CheckTransalationSuccessful"/>
    /// ever run. Lets a game-specific project deterministically strip/repair characters that can
    /// NEVER legitimately appear in a translated fragment for one specific file+column - e.g. a
    /// choice-option fragment that is always a single isolated run of source-language text with no
    /// structural separator characters in the raw text at all, so any such separator present in the
    /// *translated* fragment is unambiguously an LLM artifact and can be stripped outright rather
    /// than merely detected-and-retried via <see cref="CustomColumnValidator"/>. This prevents the
    /// structural corruption at the source instead of relying on a retry loop to eventually avoid
    /// it. Receives (textFile, column, raw, result) with the same semantics as
    /// <see cref="CustomColumnValidator"/>, and must return the (possibly repaired) result. Left
    /// null (no-op) unless a caller opts in.
    /// </summary>
    public Func<TextFileToSplit?, int?, string, string, string>? CustomColumnRepair { get; set; }

    /// <summary>
    /// Invoked once per column while <see cref="Workflow.QualityReviewWorkflow.RunAsync"/> is
    /// building its work-item list, BEFORE any LLM call - lets a game-specific project keep a
    /// column out of the quality review pass entirely, even though its file otherwise has
    /// <see cref="TextFileToSplit.EnableQualityReview"/> set. Exists for text that is structurally
    /// opaque to a QC model despite looking like ordinary translated prose - e.g. this game's
    /// dynamic-string dialogue-choice entries, where the raw/translated cell is
    /// <c>"{label};FunctionName"</c> (a real runtime choice-routing record, not a sentence) and a
    /// QC model has no way to know the ';FunctionName' suffix is an opaque identifier rather than
    /// something to rewrite/"fix". Unlike <see cref="CustomColumnValidator"/> (which only catches a
    /// BAD correction after the LLM call already happened), this hook skips the LLM call
    /// altogether for a column that should never be reviewed in the first place - cheaper, and
    /// removes any chance of a QC model corrupting a structural literal that has no validator
    /// registered for it. Receives (textFile, column, raw) - column is the zero-based CSV column
    /// index when known (null outside a column context, e.g. a plain dynamic-string/prefab-text
    /// entry). Return true to exclude the column from this run entirely (never Skipped-and-retried
    /// - it simply never becomes a work item); return false (or leave the hook null) for normal
    /// review. Left null (no-op) unless a caller opts in.
    /// </summary>
    public Func<TextFileToSplit, int?, string, bool>? CustomQcExclusionRule { get; set; }

    /// <summary>
    /// Invoked once per packaged cell/line, at the end of <see cref="Utility.PackagingTextFixups.Apply"/>
    /// - i.e. after every standard, game-agnostic packaging-time fixup (hyphen-undo, literal-"\n"
    /// undo) has already run. Lets a game-specific project add its own deterministic packaging-time
    /// text repair without needing its own duplicate call site in <see cref="Workflow.PrefabTextWorkflow"/>,
    /// <see cref="Workflow.DynamicStringWorkflow"/>, and <see cref="Workflow.CsvGameDataWorkflow"/> -
    /// registering the hook once here is enough for it to run everywhere packaging happens. Receives
    /// (textFile, column, raw, result) with the same semantics as <see cref="CustomColumnRepair"/>
    /// (textFile/column are null when packaging a plain PrefabText/DynamicString entry outside a CSV
    /// column context) and must return the (possibly repaired) result. Left null (no-op) unless a
    /// caller opts in.
    /// </summary>
    public Func<TextFileToSplit?, int?, string, string, string>? CustomPackagingFixup { get; set; }
}
