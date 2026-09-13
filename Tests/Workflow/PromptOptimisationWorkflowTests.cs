using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

/// <summary>
/// Manual, live-LLM workflow steps (not CI-safe - each Fact below makes real Ollama calls and
/// rewrites source files) - same convention as the numbered manual Facts in DragonHierOverLlm's
/// TranslationWorkflowTests.cs. Run one Fact at a time from the test explorer, then review the
/// result with `git diff` in this repo before committing or discarding it. See
/// PromptOptimisationWorkflow's doc comment for what it actually does.
/// </summary>
public class PromptOptimisationWorkflowTests
{
    // 3 levels up from the test binary's output directory reaches the Tests project root (see
    // ConfigurationTests' `workingDirectory` constant); one more reaches the repo root, which is
    // the parent of both Tests/ and FanslationStudio.LlmKit/.
    private const string BaseFilesSourceRoot = "../../../../FanslationStudio.LlmKit/BaseFiles";

    //[Fact(DisplayName = "Optimise Qwen25's own prompt set")]
    //public async Task OptimiseQwen25PromptSet()
    //{
    //    await PromptOptimisationWorkflow.RunAsync(ModelPreset.Qwen25, ModelPresetType.Standard, BaseFilesSourceRoot);
    //}

    //[Fact(DisplayName = "Optimise Qwen38's own prompt set")]
    //public async Task OptimiseQwen38PromptSet()
    //{
    //    await PromptOptimisationWorkflow.RunAsync(ModelPreset.Qwen38, ModelPresetType.Standard, BaseFilesSourceRoot);
    //}


    //[Fact(DisplayName = "Optimise Glm4's own prompt set")]
    //public async Task OptimiseGlm4PromptSet()
    //{
    //    await PromptOptimisationWorkflow.RunAsync(ModelPreset.Glm4, ModelPresetType.Standard, BaseFilesSourceRoot);
    //}

    //// Targeted re-run for the two files that failed validation (and were left untouched) on the
    //// last full Glm4 pass - BaseSystemPrompt and BaseQualityReviewPrompt are the ones that matter
    //// most for a real QC-model comparison, so they're worth the extra retry attempts on their own
    //// rather than being silently skipped alongside a full re-run that would also re-shrink the
    //// already-committed, already-accepted rewrites of every other file.
    //[Fact(DisplayName = "Optimise Glm4's System/QC prompts only")]
    //public async Task OptimiseGlm4SystemAndQcPrompts()
    //{
    //    await PromptOptimisationWorkflow.RunAsync(ModelPreset.Glm4, ModelPresetType.Standard, BaseFilesSourceRoot,
    //        promptKeys: ["BaseSystemPrompt", "BaseQualityReviewPrompt"]);
    //}
}
