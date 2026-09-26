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

    //[Fact(DisplayName = "Optimise HyMT2's own prompt set")]
    //public async Task OptimiseMT2PromptSet()
    //{
    //    await PromptOptimisationWorkflow.RunAsync(ModelPreset.HyMT2, ModelPresetType.Standard, BaseFilesSourceRoot);
    //}

    //[Fact(DisplayName = "Optimise HyMT2's own prompt set")]
    //public async Task OptimiseMT2MoePromptSet()
    //{
    //    await PromptOptimisationWorkflow.RunAsync(ModelPreset.HyMT2Moe, ModelPresetType.Standard, BaseFilesSourceRoot);
    //}

}
