using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace Tests;

/// <summary>Real lines from PathOfKungfuOverLlm's Files/Converted.</summary>
public class PersonShiftDetectorTests
{
    [Theory(DisplayName = "PersonShiftDetector catches a speaker moved onto the listener and narration")]
    [InlineData("夫君曾有一老师，乃是朝廷清流重臣，夫君对他颇为尊崇。", "Your husband once had a teacher who was a high-ranking official. Your husband held him in great respect.", PersonShiftDetector.SpouseTermAsYours)]
    [InlineData("被你爹一直囚在这里，不人不鬼过了这些年。", "After all these years, locked up here by my father.", PersonShiftDetector.SecondPersonLost)]
    [InlineData("“……你勾结螣蛇蛊主意欲何为？”", "It seems there was an instruction to correct a previous output, but no specific previous output was given.", PersonShiftDetector.ModelNarration)]
    [InlineData("恍惚间不知过了多久，你只觉周身上下真气奔涌。", "Ah, ... I wonder what there is to do around here. I only feel True Qi surging throughout my body.", PersonShiftDetector.ModelNarration)]
    [InlineData("{firstName}{playerTitle}先人后己，隐有古侠之风，孙某先谢过了……", "{playerTitle} {firstName}, you show great courtesy, with the air of an ancient hero. Sun Mou thanks you in advance...", PersonShiftDetector.FirstPersonLost)]
    [InlineData("妾身愿随夫君左右。", "You are willing to stay by his side.", PersonShiftDetector.FirstPersonLost)]
    public void CatchesShift(string raw, string translated, string expected) =>
        Assert.Equal(expected, PersonShiftDetector.Find(raw, translated));

    // The husband speaking, an addressed listener, a self-reference title, 你我 ("we"), 与你 ("with you").
    [Theory(DisplayName = "PersonShiftDetector leaves correct translations alone")]
    [InlineData("贤妻，可是有心事烦忧？为夫愿为贤妻分担，倾听贤妻心声。", "Dear wife, do you have worries on your mind? Your husband is here to share your burden.")]
    [InlineData("小女子这厢谢过了。", "This humble woman thanks you.")]
    [InlineData("只盼夫君勿忘昔日之言……梦秋告辞。", "Only pray that my husband does not forget his words of old...... Dream Chilly takes her leave.")]
    [InlineData("与你分道扬镳，再非夫妻了。", "From now on, we part ways and are no longer husband and wife.")]
    [InlineData("你我既已深情相许，倾心相交。", "Since we have pledged our deep affection to each other, we can speak our minds freely.")]
    [InlineData("此地，便是弃身冢。", "Here is the Abandoned Tomb.")]
    public void LeavesCorrectLinesAlone(string raw, string translated) =>
        Assert.Null(PersonShiftDetector.Find(raw, translated));

    [Fact(DisplayName = "PersonShiftDetector takes a game's own spouse term")]
    public void ExtraSpouseTerm() =>
        Assert.Equal(PersonShiftDetector.SpouseTermAsYours, PersonShiftDetector.Find("官人莫要走。", "Your husband, do not go.", ["官人"]));

    [Fact(DisplayName = "PersonShiftDetector reads Converted, judges what ships, and reports first-pass narration QC replaced")]
    public async Task FindCandidates_ReadsConverted()
    {
        var work = Path.Combine(Path.GetTempPath(), "PersonShiftDetectorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory($"{work}/Converted");
        try
        {
            var lines = new List<TranslationLine>
            {
                // QC replaced the narration: ships fine, but the first-pass text is still reported.
                new() { Raw = "a", Splits = [new() { Text = "单研一道，便能反推出禁蛊之术", Translated = "Ah, ...\\nI wonder what Lord Hundred Eyes is up to now.", QcTranslated = "Studying one path lets one reverse-engineer the gu ban.", QcStatus = QcStatus.Corrected }] },
                new() { Raw = "b", Splits = [new() { Text = "夫君曾有一老师", Translated = "Your husband once had a teacher.", QcStatus = QcStatus.Passed, QcQualityScore = 100 }] },
                new() { Raw = "c", Splits = [new() { Text = "此地，便是弃身冢。", Translated = "Here is the Abandoned Tomb." }] },
            };
            File.WriteAllText($"{work}/Converted/Test.csv.yaml", FanslationStudio.LlmKit.Utility.YamlHelper.CreateSerializer().Serialize(lines));

            var candidates = await PersonShiftDetector.FindCandidatesAsync(work, [new TextFileToSplit { Path = "Test.csv" }]);

            Assert.Equal([PersonShiftDetector.FirstPassNarration, PersonShiftDetector.SpouseTermAsYours], candidates.Select(c => c.Rule).Order());
            Assert.Contains("SpouseTermAsYours=1", PersonShiftDetector.FormatReport(candidates));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
