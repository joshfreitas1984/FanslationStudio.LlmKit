using FanslationStudio.LlmKit.Workflow;

namespace Tests;

public class QcInventedStutterTests
{
    [Fact(DisplayName = "A correction that adds a stutter to a source without one is rejected")]
    public void AddedStutter_NoSourceStutter_Rejected()
    {
        var reason = QualityControlWorkflow.CheckInventedStutter(
            "思阁主稍安勿躁，\\n此事有两条证据足以表明",
            "Pavilion Master Si, please stay calm",
            "Pavilion Master Si, please stay calm, c-c-calm");

        Assert.NotNull(reason);
    }

    [Theory(DisplayName = "A new word with v after j/q/x/y is rejected as malformed Pinyin")]
    [InlineData("Curly-Coated Colt", "Quanmaojv")]
    [InlineData("Old Man Jiang Er", "Jiang Xvan")]
    public void MalformedPinyin_Rejected(string baseline, string candidate) =>
        Assert.NotNull(QualityControlWorkflow.CheckInvalidPinyin(baseline, candidate));

    [Theory(DisplayName = "Valid Pinyin, ordinary English and pre-existing slips are not rejected")]
    [InlineData("Curly-Coated Colt", "Quanmao Ju")]
    [InlineData("Old Man Jiang Er", "Jiang Lao'er")]
    [InlineData("Quanmaojv", "Quanmaojv Horse")]
    [InlineData("Dragon", "Silver elvish blade")]
    public void ValidPinyinOrExisting_Allowed(string baseline, string candidate) =>
        Assert.Null(QualityControlWorkflow.CheckInvalidPinyin(baseline, candidate));

    [Fact(DisplayName = "A correction that turns 师姐我的#PlayerName# into a third party is rejected")]
    public void SelfReferenceRegression_Rejected() =>
        Assert.NotNull(QualityControlWorkflow.CheckSelfReferenceRegression(
            "这么生分做什么，你可是师姐我的#PlayerName#呀！",
            "What's with being so formal? You're my #PlayerName#, after all!",
            "What's with being so formal? You're my Senior Sister's #PlayerName#, after all!"));

    [Fact(DisplayName = "A self-reference defect already in the baseline does not block the correction")]
    public void SelfReferenceAlreadyLost_Allowed() =>
        Assert.Null(QualityControlWorkflow.CheckSelfReferenceRegression(
            "师傅我今日必胜",
            "Your Master will win today.",
            "Your Master will surely win today."));

    [Fact(DisplayName = "A stutter the source really has may be added")]
    public void AddedStutter_SourceStutter_Allowed()
    {
        Assert.Null(QualityControlWorkflow.CheckInventedStutter("咳咳，大、大姐，这二位是我的朋友。", "Cough cough, big sister", "Cough cough, B-b-big sister"));
    }

    [Fact(DisplayName = "A stutter already in the baseline is not counted as added")]
    public void ExistingStutter_Allowed()
    {
        Assert.Null(QualityControlWorkflow.CheckInventedStutter("你好", "S‑sure", "S‑sure, hello"));
    }
}
