using System.Text;
using System.Text.RegularExpressions;
using FanslationStudio.LlmKit.Support;

namespace FanslationStudio.LlmKit.Utility;

/// <summary>
/// Detect-only checks for a translation that moved a line between speakers ("my husband" became "your husband", 你 became "I"),
/// for a Chinese source and an English translation. Each rule is source-grounded and deliberately narrow, but a line alone cannot
/// say who is speaking, so a hit is a candidate for a human, never proof: this is a report (<see cref="FindCandidatesAsync"/>), not
/// part of <see cref="LineValidation.CheckTransalationSuccessful"/>, where a false positive would retry or reject a good line in
/// every game. Narration (<see cref="LineValidation.FindModelNarration"/>) is the exception - never a valid translation - and is
/// reported here under <see cref="ModelNarration"/> and <see cref="FirstPassNarration"/>.
/// </summary>
public static partial class PersonShiftDetector
{
    public const string SpouseTermAsYours = "SpouseTermAsYours";
    public const string FirstPersonLost = "FirstPersonLost";
    public const string SecondPersonLost = "SecondPersonLost";
    public const string ModelNarration = "ModelNarration";
    public const string FirstPassNarration = "FirstPassNarration";

    /// <summary>One flagged split: where it is, what shipped, and which rule it broke.</summary>
    public sealed record PersonShiftCandidate(string FilePath, string Raw, string Source, string Translated, string Rule, QcStatus QcStatus, int? QcScore);

    /// <summary>
    /// The first rule the pair breaks, or null.
    /// </summary>
    /// <param name="raw">The Chinese source.</param>
    /// <param name="translated">The English translation.</param>
    /// <param name="extraSpouseTerms">A wife's words for her husband that this game uses beyond 夫君/相公/郎君.</param>
    public static string? Find(string raw, string translated, IReadOnlyCollection<string>? extraSpouseTerms = null)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(translated))
            return null;

        // The translator or QC corrector talking about its task: never related to its source.
        if (LineValidation.FindModelNarration(raw, translated) != null)
            return ModelNarration;

        var hasYou = YouRegex().IsMatch(translated);
        var hasI = FirstPersonRegex().IsMatch(translated);
        // 你我 is "you and I" / "we": it is both people, not the listener alone.
        var sourceSecond = SecondPersonSourceRegex().IsMatch(raw.Replace("你我", ""));
        var sourceFirst = FirstPersonSourceRegex().IsMatch(raw);

        // 夫君/相公 are what a wife calls her husband, so "your husband" is the speaker shifted onto the listener. A line where
        // the husband refers to himself (为夫) or addresses the wife (娘子/贤妻) does legitimately say "your".
        var spouseTerm = SpouseTermRegex().IsMatch(raw) || extraSpouseTerms?.Any(term => raw.Contains(term, StringComparison.Ordinal)) == true;
        if (spouseTerm && !HusbandSelfRegex().IsMatch(raw) && YourSpouseRegex().IsMatch(translated))
            return SpouseTermAsYours;

        // Self-reference words (妾身, 奴家, 在下...) with no 你 in the source, rendered as "you" with no "I/my/we" and no
        // "this humble woman" style stand-in: the speaker has been turned into the listener.
        if (sourceFirst && !sourceSecond && hasYou && !hasI && !SelfStandInRegex().IsMatch(translated))
            return FirstPersonLost;

        // 你/您 with no first person in the source, rendered with "I/my/me" and no "you": the listener has been turned into the speaker.
        if (sourceSecond && !sourceFirst && !BareFirstPersonSourceRegex().IsMatch(raw) && hasI && !hasYou)
            return SecondPersonLost;

        return null;
    }

    /// <summary>
    /// Every split in <c>{workingDirectory}/Converted</c> that breaks a rule, judged on what packaging ships (the QC correction when there
    /// is one, else the first-pass translation). A split whose first-pass translation is narration is also reported
    /// (<see cref="FirstPassNarration"/>) even when QC replaced it: it comes back the moment QC is off or the line is retranslated.
    /// Read-only; no LLM calls.
    /// </summary>
    public static async Task<List<PersonShiftCandidate>> FindCandidatesAsync(string workingDirectory, TextFileToSplit[] textFiles, IReadOnlyCollection<string>? extraSpouseTerms = null)
    {
        var candidates = new List<PersonShiftCandidate>();

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory, textFiles, (_, textFile, fileLines) =>
        {
            foreach (var line in fileLines)
            foreach (var split in line.Splits)
            {
                var shipped = string.IsNullOrEmpty(split.QcTranslated) ? split.Translated : split.QcTranslated;
                var rule = Find(split.Text, shipped, extraSpouseTerms)
                    ?? (LineValidation.FindModelNarration(split.Text, split.Translated) != null ? FirstPassNarration : null);
                if (rule != null)
                    candidates.Add(new PersonShiftCandidate(textFile.Path, line.Raw, split.Text, shipped, rule, split.QcStatus, split.QcQualityScore));
            }

            return Task.CompletedTask;
        });

        return candidates;
    }

    /// <summary>A text report: a count per rule, then one entry per candidate.</summary>
    public static string FormatReport(IReadOnlyList<PersonShiftCandidate> candidates)
    {
        var report = new StringBuilder();
        report.AppendLine(string.Join(", ", candidates.GroupBy(c => c.Rule).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));
        report.AppendLine();
        foreach (var candidate in candidates)
        {
            report.AppendLine($"[{candidate.Rule}] {candidate.FilePath} qc={candidate.QcStatus}/{candidate.QcScore}");
            report.AppendLine($"  raw:   {candidate.Raw}");
            report.AppendLine($"  src:   {candidate.Source}");
            report.AppendLine($"  trans: {candidate.Translated}");
        }

        return report.ToString();
    }

    [GeneratedRegex(@"\b(?:you|your|yours|yourself|you're|you'll|you've|you'd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex YouRegex();

    [GeneratedRegex(@"\bI(?:'m|'ll|'ve|'d)?\b|\b(?:me|my|mine|myself|we|we're|us|our|ours)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FirstPersonRegex();

    // 阁下 and titles are second person too, but are written as titles in English, so only the pronouns count.
    [GeneratedRegex(@"[你您汝尔]")]
    private static partial Regex SecondPersonSourceRegex();

    // 我 itself is left out: it sits inside 无我, 自我, 我们 (still first person) and idioms, and the lines with a bare 我 already have "I".
    [GeneratedRegex(@"妾身|奴家|小女子|在下(?![个一面方午次去来风雨雪])|老夫|老朽|本座|本宫|愚兄|为兄|小弟(?!们)|孩儿")]
    private static partial Regex FirstPersonSourceRegex();

    // With 我/咱/俺 beside the 你, the English "I" is the speaker and the 你 may sit in a "with you"/"to you" the translation folds into "we"/"let's".
    [GeneratedRegex(@"[我咱俺吾]|[与同跟和]你")]
    private static partial Regex BareFirstPersonSourceRegex();

    [GeneratedRegex(@"夫君|相公|郎君")]
    private static partial Regex SpouseTermRegex();

    [GeneratedRegex(@"为夫|娘子|贤妻|吾妻|爱妻")]
    private static partial Regex HusbandSelfRegex();

    [GeneratedRegex(@"\byour (?:husband|hubby|beloved husband)\b", RegexOptions.IgnoreCase)]
    private static partial Regex YourSpouseRegex();

    [GeneratedRegex(@"\b(?:this|the|an?) (?:humble |lowly |old |unworthy |young )?(?:one|woman|maiden|girl|man|servant|brother|sister|fellow)\b|\b(?:your|this) (?:humble )?(?:servant|brother|sister|elder brother|younger brother)\b|\bGrandma\b|\bUncle\b|\bBrother\b", RegexOptions.IgnoreCase)]
    private static partial Regex SelfStandInRegex();
}
