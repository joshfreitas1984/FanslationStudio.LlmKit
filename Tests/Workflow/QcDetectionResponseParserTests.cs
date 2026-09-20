using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Workflow;

namespace Tests.Workflow;

public sealed class QcDetectionResponseParserTests
{
    [Fact]
    public void Parse_MultipleDefects()
    {
        var result = QcDetectionResponseParser.Parse("DEFECTS: DROPPED_CONTENT, HARD_TO_PARSE_SEAM");

        Assert.True(result.Success);
        Assert.Equal(
            [QcDefectCategory.DroppedContent, QcDefectCategory.HardToParseSeam],
            result.Findings.Select(finding => finding.Category));
    }

    [Fact]
    public void Parse_None()
    {
        var result = QcDetectionResponseParser.Parse("DEFECTS: NONE");

        Assert.True(result.Success);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Parse_UncertainAlone_IsAValidFindingNotDiscarded()
    {
        // UNCERTAIN means "something's off but not confident enough to name it" - a real signal
        // that must block auto-pass, never treated as unparseable or collapsed into NONE.
        var result = QcDetectionResponseParser.Parse("DEFECTS: UNCERTAIN");

        Assert.True(result.Success);
        Assert.True(result.HasDefects);
        Assert.Equal([QcDefectCategory.Uncertain], result.Findings.Select(finding => finding.Category));
    }

    [Fact]
    public void Parse_UncertainMixedWithNamedCategory_IsInvalid()
    {
        var result = QcDetectionResponseParser.Parse("DEFECTS: UNCERTAIN, DOMAIN_TERM");

        Assert.False(result.Success);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Merge_DeduplicatesFindingsInDetectorOrder()
    {
        var first = QcDetectionResponseParser.Parse("DEFECTS: DROPPED_CONTENT, HARD_TO_PARSE_SEAM");
        var second = QcDetectionResponseParser.Parse("DEFECTS: HARD_TO_PARSE_SEAM, DOMAIN_TERM");

        var result = QcDetectionResult.Merge(first, second);

        Assert.True(result.Success);
        Assert.Equal(
            [QcDefectCategory.DroppedContent, QcDefectCategory.HardToParseSeam, QcDefectCategory.DomainTerm],
            result.Findings.Select(finding => finding.Category));
    }

    [Fact]
    public void Merge_FailsWhenAnyDetectorResultFailed()
    {
        var valid = QcDetectionResponseParser.Parse("DEFECTS: DROPPED_CONTENT");
        var invalid = QcDetectionResponseParser.Parse("DEFECTS: UNKNOWN_CATEGORY");

        var result = QcDetectionResult.Merge(valid, invalid);

        Assert.False(result.Success);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("DEFECTS:")]
    [InlineData("DEFECTS: NONE, DROPPED_CONTENT")]
    [InlineData("DEFECTS: DROPPED_CONTENT, DROPPED_CONTENT")]
    [InlineData("DEFECTS: MADE_UP_CATEGORY")]
    public void Parse_InvalidResponse(string response)
    {
        var result = QcDetectionResponseParser.Parse(response);

        Assert.False(result.Success);
        Assert.Empty(result.Findings);
    }
}