using FanslationStudio.LlmKit.Configuration;

namespace FanslationStudio.LlmKit.Tests.Configuration;

public class ConfigurationTests
{
    const string workingDirectory = "../../../Configuration";

    [Fact]
    public void TestLoadConfiguration()
    {
        var llmConfig = ConfigurationExtensions.GetConfiguration($"{workingDirectory}/Workspace1");

        Assert.Equal("qwen2.5:7b", llmConfig.Runtime.Models["Standard"].Model);
        Assert.Equal("http://localhost/standard", llmConfig.Runtime.Models["Standard"].Url);
        Assert.Equal("StandardApiKey", llmConfig.Runtime.Models["Standard"].ApiKey);
        Assert.False(llmConfig.Runtime.Models["Standard"].ApiKeyRequired);

        Assert.Equal("qwen2.5:7b", llmConfig.Runtime.Models["Structured"].Model);
        Assert.Equal("http://localhost/structured", llmConfig.Runtime.Models["Structured"].Url);
        Assert.Equal("StructuredApiKey", llmConfig.Runtime.Models["Structured"].ApiKey);
        Assert.True(llmConfig.Runtime.Models["Structured"].ApiKeyRequired);

        Assert.Single(llmConfig.Runtime.ManualTranslations);
        Assert.Equal("Overriden Prompt 1", llmConfig.Runtime.Models["Standard"].Prompts["BaseSystemSuffixPrompt"]);
        Assert.Equal("Overriden Prompt 2", llmConfig.Runtime.Models["Structured"].Prompts["BaseCorrectionSuffixPrompt"]);
        
        // Custom Glossary
        Assert.NotNull(llmConfig.Runtime.GlossaryLines.Where(g => g.Raw == "風雷神腳").Single());

        // Removed Phonetics
        Assert.Null(llmConfig.Runtime.GlossaryLines.Where(g => g.Raw == "拍拍").FirstOrDefault());

        // Overriden Items
        var overriden1 = llmConfig.Runtime.GlossaryLines.Where(g => g.Raw == "攻击力").Single();
        Assert.True(overriden1.CheckForBadTranslation);

        var overriden2 = llmConfig.Runtime.GlossaryLines.Where(g => g.Raw == "防御力").Single();
        Assert.Equal("Guard", overriden2.Result);
    }
}
