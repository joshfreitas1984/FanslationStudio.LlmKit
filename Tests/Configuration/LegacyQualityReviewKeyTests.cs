using FanslationStudio.LlmKit.Configuration;

namespace FanslationStudio.LlmKit.Tests.Configuration;

public class LegacyQualityReviewKeyTests
{
    private const string Models = "models:\n  - name: Standard\n    model: m\n    url: \"http://localhost/t\"\n";

    private static LlmConfig Load(string yaml, Action<string>? arrange = null)
    {
        var dir = Directory.CreateTempSubdirectory("llmkit-legacy-key-").FullName;
        try
        {
            File.WriteAllText($"{dir}/Config.yaml", Models + yaml);
            arrange?.Invoke(dir);
            return ConfigurationExtensions.GetConfiguration(dir);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact(DisplayName = "Legacy qualityReview: key maps onto QualityControl")]
    public void LegacyKeyIsHonoured()
    {
        var config = Load("qualityReview:\n  enabled: true\n  minAcceptableScore: 55\n");
        Assert.True(config.QualityControl.Enabled);
        Assert.Equal(55, config.QualityControl.MinAcceptableScore);
    }

    [Fact(DisplayName = "qualityControl: key wins over a legacy qualityReview: key")]
    public void NewKeyWins()
    {
        var config = Load("qualityReview:\n  enabled: false\nqualityControl:\n  enabled: true\n");
        Assert.True(config.QualityControl.Enabled);
    }
}
