using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public sealed class QcDefectCategoryTokensTests
{
    [Fact]
    public void TryParseList_None_IsEmptySuccess()
    {
        Assert.True(QcDefectCategoryTokens.TryParseList(" none ", out var categories));
        Assert.Empty(categories);
    }

    [Fact]
    public void TryParseList_DistinctTokens_ParsesInOrder()
    {
        Assert.True(QcDefectCategoryTokens.TryParseList("DOMAIN_TERM, dropped_content", out var categories));
        Assert.Equal([QcDefectCategory.DomainTerm, QcDefectCategory.DroppedContent], categories);
    }

    [Theory]
    [InlineData("DOMAIN_TERM, NONE")]
    [InlineData("DOMAIN_TERM, DOMAIN_TERM")]
    [InlineData("NOT_A_CATEGORY")]
    [InlineData("")]
    [InlineData(" , ")]
    public void TryParseList_ProtocolViolations_Fail(string value)
    {
        Assert.False(QcDefectCategoryTokens.TryParseList(value, out _));
    }

    [Fact]
    public void VerificationParse_OverflowingScore_ClampsInsteadOfThrowing()
    {
        var result = QcVerificationResponseParser.Parse(
            "UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 99999999999999999999", [QcDefectCategory.DomainTerm]);

        Assert.True(result.Success);
        Assert.Equal(100, result.Score);
    }
}
