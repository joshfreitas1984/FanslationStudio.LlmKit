using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Assessments;

/// <summary>A split a game's QC pass flagged or failed to correct: raw material for a gold case.</summary>
public sealed record FlaggedSplit(
    string Game, string SourceFile, string Source, string Translation,
    QcStatus Status, bool Flagged, int? Score, QcDefectCategory Category, string Note);

/// <summary>
/// Mines each registered game's flagged and FailedValidation splits into candidate schema-v2 gold cases
/// (game, sourceFile, glossary snapshot filled in). Read-only; a human still writes the label, the defect
/// categories and the review note before a candidate joins the gold set.
/// </summary>
public static class GoldCaseMining
{
    public static List<FlaggedSplit> ReadFlagged(string game, string filesPath)
    {
        var directory = Path.Combine(filesPath, "Converted");
        var result = new List<FlaggedSplit>();
        if (!Directory.Exists(directory))
            return result;

        var deserializer = YamlHelper.CreateDeserializer();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            List<TranslationLine> lines;
            try { lines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(file)) ?? []; }
            catch (YamlDotNet.Core.YamlException) { continue; }

            var name = Path.GetFileName(file);
            foreach (var split in lines.SelectMany(l => l.Splits))
            {
                if (string.IsNullOrWhiteSpace(split.Text) || string.IsNullOrWhiteSpace(split.Translated))
                    continue;
                if (!split.FlaggedForQcReview && split.QcStatus != QcStatus.FailedValidation)
                    continue;

                var note = split.QcFailureReason;
                result.Add(new FlaggedSplit(game, name, split.Text, split.Translated, split.QcStatus,
                    split.FlaggedForQcReview, split.QcQualityScore, split.QcDefectCategory, note));
            }
        }
        return result;
    }

    /// <summary>
    /// Up to <paramref name="perGroup"/> candidates per game and defect category (spread across files, the
    /// lowest scores first), skipping sources already in <paramref name="existingSources"/>.
    /// </summary>
    public static List<CandidateCase> Select(IEnumerable<GameCorpus> games, Func<GameCorpus, List<FlaggedSplit>> flagged,
        ISet<string> existingSources, int perGroup)
    {
        var candidates = new List<CandidateCase>();
        foreach (var game in games)
            foreach (var group in flagged(game)
                         .Where(f => !existingSources.Contains(f.Source))
                         .GroupBy(f => f.Category))
            {
                var picked = group
                    .GroupBy(f => f.SourceFile).SelectMany(g => g.OrderBy(f => f.Score ?? -1).Take(2))
                    .OrderBy(f => f.Score ?? -1).Take(perGroup);
                foreach (var f in picked)
                    candidates.Add(ToCandidate(game, f));
            }
        return candidates;
    }

    private static CandidateCase ToCandidate(GameCorpus game, FlaggedSplit f) => new()
    {
        SampleId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{f.Game}|{f.SourceFile}|{f.Source}"))).ToLowerInvariant()[..16],
        Game = f.Game,
        SourceFile = f.SourceFile,
        Source = f.Source,
        CurrentTranslation = f.Translation,
        Glossary = GlossaryLine.SelectFor(f.Source, game.Glossary, string.Empty).Select(g => g.ToSnapshot()).ToList(),
        QcStatus = f.Status.ToString(),
        QcQualityScore = f.Score,
        QcDefectCategory = f.Category.ToString(),
        QcNote = f.Note,
    };
}

/// <summary>A mined gold-case candidate. Label fields are left empty for the human reviewer.</summary>
public sealed class CandidateCase
{
    public string SampleId { get; set; } = string.Empty;
    public string Game { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string CurrentTranslation { get; set; } = string.Empty;
    public List<GlossaryLine> Glossary { get; set; } = [];

    // What the game's QC pass said, for context only - never copy it into the label (circularity).
    public string QcStatus { get; set; } = string.Empty;
    public int? QcQualityScore { get; set; }
    public string QcDefectCategory { get; set; } = string.Empty;
    public string QcNote { get; set; } = string.Empty;

    // The reviewer fills these in: Pass | Defect | Abstain, the categories, and a one-line reason.
    public string Label { get; set; } = string.Empty;
    public List<string> DefectCategories { get; set; } = [];
    public string ReviewNote { get; set; } = string.Empty;
}
