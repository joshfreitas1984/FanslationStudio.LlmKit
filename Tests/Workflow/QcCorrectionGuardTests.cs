using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public class QcCorrectionGuardTests
{
    [Theory(DisplayName = "IsEquivalentText ignores non-breaking hyphens and whitespace only")]
    [InlineData("half‑dead, yes", "half-dead, yes", true)]
    [InlineData("a  b", "a b", true)]
    [InlineData("half-dead", "half-alive", false)]
    public void IsEquivalentText(string a, string b, bool expected) =>
        Assert.Equal(expected, QualityControlWorkflow.IsEquivalentText(a, b));

    [Fact(DisplayName = "CheckRunawayRepetition rejects new long letter runs only")]
    public void RunawayRepetition()
    {
        Assert.NotNull(QualityControlWorkflow.CheckRunawayRepetition("Who are you", "Waaah who are youuuuuuuuuuuuu"));
        Assert.Null(QualityControlWorkflow.CheckRunawayRepetition("Who are you", "Who are you?!"));
        Assert.Null(QualityControlWorkflow.CheckRunawayRepetition("Ahhhhhhhhhhhh", "Ahhhhhhhhhhhh!"));
    }
}
