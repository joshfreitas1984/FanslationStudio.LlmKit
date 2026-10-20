using FanslationStudio.LlmKit;

namespace Tests;

/// <summary>
/// 某 after a surname (李某, 孙某, 我韩某) stands in for a name, usually the speaker saying "I". A translation that prints
/// it as "Li Mou" / "Zhao Mouxia" / "Lv Mouren" invents a second name and used to pass QC. The lines are real ones from
/// the Path of Kungfu, Legend of Mortal and Dragon Hier corpora.
/// </summary>
public class SurnameMouTests
{
    [Theory(DisplayName = "FindSurnameMou finds the invented name a 某 was turned into")]
    [InlineData("孙某先谢过了……", "Sun Mou thanks you in advance...", "Sun Mou")]
    [InlineData("不错，魏某昔年下野乃陛下授意。", "Not bad. Wei Mou's retirement from politics was at the emperor's behest.", "Wei Mou")]
    [InlineData("我韩某却也非任人宰割之辈。", "But I, Han Mou, am not someone's meat either.", "Han Mou")]
    [InlineData("我赵某侠是要趁她不能动弹，跟她拜堂成亲！", "I, Zhao Mouxia, intend to take advantage of her inability to move and marry her!", "Zhao Mouxia")]
    [InlineData("我吕某人纵横商场几十年。", "I, Lv Mouren, have traversed the business world for decades.", "Lv Mouren")]
    [InlineData("杜某无此谋略，此乃星雨之计。", "Du Mou lacks such strategy; this is Xingyu's plan.", "Du Mou")]
    public void FindsTheInventedName(string raw, string result, string expected) =>
        Assert.Equal(expected, LineValidation.FindSurnameMou(raw, result));

    [Theory(DisplayName = "FindSurnameMou leaves a good rendering of 某 alone")]
    [InlineData("……好。襄阳城北，谢某自去待死。", "...Fine. At the northern part of Xiangyang City, I, Xie, will go to await my death.")]
    [InlineData("正是余某。", "It is I, Yu")]
    [InlineData("陆某在此，要杀要剐，冲我来便是！", "Lu here. If you wish to kill or torture me, do it!")]
    [InlineData("那李某昨夜来过，说是要找你。", "That Li fellow came by last night and said he was looking for you.")]
    public void LeavesGoodRenderingsAlone(string raw, string result) =>
        Assert.Null(LineValidation.FindSurnameMou(raw, result));

    [Theory(DisplayName = "FindSurnameMou leaves a real Mou name alone: 牟, or a line with no 某")]
    [InlineData("牟前辈在此隐居多年。", "Senior Mou has lived here in seclusion for many years.")]
    [InlineData("牟前辈说李某不在。", "Senior Mou said Li Mou is out.")]
    [InlineData("吾乃“智计无双”吴谋，", "I am Wu Mou, the peerless schemer,")]
    public void LeavesARealNameAlone(string raw, string result) =>
        Assert.Null(LineValidation.FindSurnameMou(raw, result));
}
