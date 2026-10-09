namespace FanslationStudio.LlmKit.Assessments;

/// <summary>
/// A test that calls a live LLM (or reads sibling game repos) and so never runs in CI: skipped
/// unless the <c>LLMKIT_ASSESSMENTS</c> environment variable is set to 1.
/// </summary>
public sealed class ManualFactAttribute : FactAttribute
{
    public ManualFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LLMKIT_ASSESSMENTS") != "1")
            Skip = "Manual assessment: set LLMKIT_ASSESSMENTS=1 to run.";
    }
}
