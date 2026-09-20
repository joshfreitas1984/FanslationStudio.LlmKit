using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public sealed class QcVerificationResponseParserTests
{
    private static readonly QcDefectCategory[] TwoConfirmedDefects =
        [QcDefectCategory.DomainTerm, QcDefectCategory.DroppedContent];

    [Fact]
    public void Parse_AllResolvedNoNewDefects_IsAccepted()
    {
        var result = QcVerificationResponseParser.Parse(
            "UNRESOLVED: NONE\nNEW_DEFECTS: NONE\nSCORE: 92", TwoConfirmedDefects);

        Assert.True(result.Success);
        Assert.True(result.Accepted);
        Assert.Empty(result.UnresolvedDefects);
        Assert.Empty(result.NewDefects);
        Assert.Equal(92, result.Score);
    }

    [Fact]
    public void Parse_PartiallyUnresolved_IsNotAccepted()
    {
        var result = QcVerificationResponseParser.Parse(
            "UNRESOLVED: DROPPED_CONTENT\nNEW_DEFECTS: NONE\nSCORE: 40", TwoConfirmedDefects);

        Assert.True(result.Success);
        Assert.False(result.Accepted);
        Assert.Equal([QcDefectCategory.DroppedContent], result.UnresolvedDefects);
        Assert.Empty(result.NewDefects);
    }

    [Fact]
    public void Parse_NewDefectIntroduced_IsNotAcceptedEvenIfAllConfirmedResolved()
    {
        var result = QcVerificationResponseParser.Parse(
            "UNRESOLVED: NONE\nNEW_DEFECTS: GARBLED_NUMBER\nSCORE: 95", TwoConfirmedDefects);

        Assert.True(result.Success);
        Assert.False(result.Accepted);
        Assert.True(result.AllResolved);
        Assert.True(result.IntroducedNewDefect);
        Assert.Equal([QcDefectCategory.GarbledNumber], result.NewDefects);
    }

    [Fact]
    public void Parse_UnresolvedNotInConfirmedSet_IsInvalid()
    {
        // A verifier naming a category that was never part of CONFIRMED DEFECTS is a protocol
        // violation, not a real signal - see the parser's doc comment.
        var result = QcVerificationResponseParser.Parse(
            "UNRESOLVED: GARBLED_NUMBER\nNEW_DEFECTS: NONE\nSCORE: 40", TwoConfirmedDefects);

        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("UNRESOLVED: NONE\nSCORE: 90")]
    [InlineData("NEW_DEFECTS: NONE\nSCORE: 90")]
    [InlineData("UNRESOLVED: NONE\nNEW_DEFECTS: NONE")]
    [InlineData("UNRESOLVED: NONE, DOMAIN_TERM\nNEW_DEFECTS: NONE\nSCORE: 90")]
    [InlineData("UNRESOLVED: DOMAIN_TERM, DOMAIN_TERM\nNEW_DEFECTS: NONE\nSCORE: 90")]
    public void Parse_InvalidResponse(string response)
    {
        var result = QcVerificationResponseParser.Parse(response, TwoConfirmedDefects);

        Assert.False(result.Success);
    }
}
