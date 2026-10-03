namespace FanslationStudio.LlmKit.Support;

public class ValidationResult
{
    public bool Valid;
    public string Result = string.Empty;
    public string CorrectionPrompt = string.Empty;
    public bool RequiresSentenceBySentenceCorrection = false;

    /// <summary>
    /// Descriptions of failed checks that add no <see cref="CorrectionPrompt"/> (e.g. an invalid
    /// phrase or a length blow-up), so a caller reporting why a candidate failed never gets a blank
    /// reason. Diagnostic-only - never sent to a model.
    /// </summary>
    public List<string> SilentFailures = new();

    /// <summary>
    /// Set by <see cref="FanslationStudio.LlmKit.TranslationService.TranslateSplitAsync"/> when a
    /// split still failed after its normal <see cref="Configuration.LlmConfig.RetryCount"/> budget
    /// was exhausted and it was re-attempted against
    /// <see cref="Configuration.LlmConfig.EscalationModelName"/>. Diagnostic-only - lets
    /// downstream logging (e.g. the <c>UnprocessableItems.log</c> writer in
    /// <c>TranslateViaLlmAsyncPooled</c>) show whether escalation was tried at all, not just the
    /// final pass/fail outcome.
    /// </summary>
    public bool EscalationAttempted;

    public ValidationResult()
    {
    }

    public ValidationResult(bool valid, string result)
    {
        Valid = valid;
        Result = result;
    }

    public ValidationResult(string result)
    {
        Valid = !string.IsNullOrEmpty(result);
        Result = result;
    }
}