using FanslationStudio.LlmKit.Configuration;

namespace FanslationStudio.LlmKit.Tests.Configuration;

public class ConfigurationTests
{
    const string workingDirectory = "../../../Configuration";

    [Fact]
    public void TestLoadConfiguration()
    {
        var llmConfig = ConfigurationExtensions.GetConfiguration($"{workingDirectory}/Workspace1");

        Assert.Equal("qwen2.5:7b", llmConfig.ExecutionValues.StandardModel.Model);
        Assert.Equal("http://localhost/standard", llmConfig.ExecutionValues.StandardModel.Url);
        Assert.Equal("StandardApiKey", llmConfig.ExecutionValues.StandardModel.ApiKey);
        Assert.False(llmConfig.ExecutionValues.StandardModel.ApiKeyRequired);

        Assert.Equal("qwen2.5:7b", llmConfig.ExecutionValues.StructuredTextModel.Model);
        Assert.Equal("http://localhost/structured", llmConfig.ExecutionValues.StructuredTextModel.Url);
        Assert.Equal("StructuredApiKey", llmConfig.ExecutionValues.StructuredTextModel.ApiKey);
        Assert.True(llmConfig.ExecutionValues.StructuredTextModel.ApiKeyRequired);

        Assert.Single(llmConfig.ExecutionValues.ManualTranslations);
        Assert.Equal("Overriden Prompt 1", llmConfig.ExecutionValues.StandardModel.Prompts["BaseSystemSuffixPrompt"]);
        Assert.Equal("Overriden Prompt 2", llmConfig.ExecutionValues.StructuredTextModel.Prompts["BaseCorrectionSuffixPrompt"]);

        // Custom Glossary
        Assert.NotNull(llmConfig.ExecutionValues.GlossaryLines.Where(g => g.Raw == "風雷神腳").Single());

        // Removed Phonetics
        Assert.Null(llmConfig.ExecutionValues.GlossaryLines.Where(g => g.Raw == "拍拍").FirstOrDefault());

        // Overriden Items
        var overriden1 = llmConfig.ExecutionValues.GlossaryLines.Where(g => g.Raw == "攻击力").Single();
        Assert.True(overriden1.CheckForBadTranslation);

        var overriden2 = llmConfig.ExecutionValues.GlossaryLines.Where(g => g.Raw == "防御力").Single();
        Assert.Equal("Guard", overriden2.Result);
    }
}
