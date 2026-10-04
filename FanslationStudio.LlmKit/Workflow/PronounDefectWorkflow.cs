using FanslationStudio.LlmKit.Configuration;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Collections.Concurrent;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Finds, and optionally flags for retranslation, translations with pronoun defects: a he/she invented where
/// the source states no gender, a he/she that contradicts a speaker gender the game knows, and subject-less
/// narration written as "I". Meant for repairing an existing corpus translated before the prompt rules and
/// <see cref="GameHooks.LineContextProvider"/> existed - any game project can call <see cref="RunAsync"/> from
/// a one-line test. Without a line-context provider only the first and last defect can be detected.
/// </summary>
public static class PronounDefectWorkflow
{
    /// <summary>One current translation that shows an invented gender or first-person narration.</summary>
    public sealed record PronounDefectHit(string File, string Category, string Source, string Translated, bool QcRewrote);

    /// <summary>
    /// The hits; how many lines <see cref="GameHooks.LineContextProvider"/> knows the gender of and whose
    /// pronouns agree with it (<paramref name="GenderKnownAndCorrect"/>); and how many of those use only "they"
    /// (<paramref name="NeutralForKnownGender"/>) - not wrong, but a retranslation with context would be more natural.
    /// </summary>
    public sealed record PronounDefectResult(List<PronounDefectHit> Hits, int GenderKnownAndCorrect, int NeutralForKnownGender);

    /// <summary>
    /// Finds translations with the pronoun defects <see cref="LineValidation.InventsGender"/> and
    /// <see cref="LineValidation.NarratesAsFirstPerson"/> describe, judged on the pre-QC
    /// <see cref="TranslationSplit.Translated"/> (what a retranslation replaces). With
    /// <paramref name="flagForRetranslation"/> false this is a dry run and writes nothing; with true it flags
    /// each hit for retranslation (skipping lines already flagged). <see cref="PronounDefectHit.QcRewrote"/>
    /// marks lines QC already corrected, where the shipped text may differ from what was judged.
    /// With <paramref name="hooks"/> carrying a <see cref="GameHooks.LineContextProvider"/>, lines whose speaker's
    /// gender it knows are skipped (a he/she there is correct), so the count matches what a retranslation with
    /// line context enabled would still get wrong.
    /// </summary>
    public static async Task<PronounDefectResult> FindAsync(string workingDirectory,
        TextFileToSplit[] textFiles, bool flagForRetranslation, GameHooks? hooks = null)
    {
        var hits = new ConcurrentBag<PronounDefectHit>();
        var genderKnownAndCorrect = 0;
        var neutralForKnownGender = 0;
        var serializer = YamlHelper.CreateSerializer();

        await FileIteration.IterateTranslatedFilesInParallelAsync(workingDirectory,
            textFiles,
            async (outputFile, textFileToTranslate, fileLines) =>
        {
            var flagged = 0;
            var contexts = hooks?.LineContextProvider?.Invoke(workingDirectory, textFileToTranslate, fileLines);

            foreach (var line in fileLines)
                foreach (var split in line.Splits)
                {
                    if (split.FlaggedForRetranslation || split.Text.Length == 0 || split.Translated.Length == 0)
                        continue;

                    var known = contexts != null && contexts.TryGetValue(split, out var context) && context.GenderKnown ? context : null;

                    string? category;
                    if (known != null)
                    {
                        // The speaker's gender is known, so he/she is allowed - but only if it is the right one.
                        category = LineValidation.ContradictsGender(split.Text, split.Translated, known.Gender) ? "WrongGender"
                            : LineValidation.ContradictsGenderDespiteKinshipTerm(split.Text, split.Translated, known.Gender) ? "WrongGenderKinshipTerm"
                            : LineValidation.NarratesAsFirstPerson(split.Text, split.Translated) ? "NarratedAsFirstPerson"
                            : null;

                        if (category == null && LineValidation.InventsGender(split.Text, split.Translated))
                        {
                            Interlocked.Increment(ref genderKnownAndCorrect);
                            if (LineValidation.UsesOnlyNeutralPronouns(split.Translated))
                                Interlocked.Increment(ref neutralForKnownGender);
                        }
                    }
                    else
                        category = LineValidation.InventsGender(split.Text, split.Translated) ? "InventedGender"
                            : LineValidation.NarratesAsFirstPerson(split.Text, split.Translated) ? "NarratedAsFirstPerson"
                            : null;

                    if (category == null)
                        continue;

                    hits.Add(new PronounDefectHit(textFileToTranslate.Path, category, split.Text, split.Translated,
                        split.QcStatus == QcStatus.Corrected));

                    if (!flagForRetranslation)
                        continue;

                    split.FlaggedForRetranslation = true;
                    split.FlaggedMistranslation = category;
                    flagged++;
                }

            if (flagged == 0)
                return;

            await FileHelper.WriteAllTextWithRetryAsync(outputFile, serializer.Serialize(fileLines));
            Console.WriteLine($"Flagged {flagged} records in {outputFile}");
        });

        return new PronounDefectResult(hits.ToList(), genderKnownAndCorrect, neutralForKnownGender);
    }

    /// <summary>
    /// <see cref="FindAsync"/> plus a report: prints a summary and writes every hit to
    /// <c>TestResults/PronounRetranslation.yaml</c>. With <paramref name="flagForRetranslation"/> false (the
    /// default) it is a dry run that leaves <c>Converted</c> untouched, so the count can be read before committing
    /// to a retranslation. Flagged lines are not packaged until retranslated, so run the flag pass and a
    /// translate-flagged pass back to back.
    /// </summary>
    public static async Task<PronounDefectResult> RunAsync(string workingDirectory, TextFileToSplit[] textFiles,
        bool flagForRetranslation = false, GameHooks? hooks = null)
    {
        var result = await FindAsync(workingDirectory, textFiles, flagForRetranslation, hooks);
        var hits = result.Hits;

        var summary = hits
            .GroupBy(h => h.Category)
            .ToDictionary(g => g.Key, g => new
            {
                total = g.Count(),
                shippingAsIs = g.Count(h => !h.QcRewrote),
                qcRewrote = g.Count(h => h.QcRewrote),
                byFile = g.GroupBy(h => h.File).OrderByDescending(f => f.Count()).ToDictionary(f => f.Key, f => f.Count()),
            });

        var report = new
        {
            mode = flagForRetranslation ? "flagged for retranslation" : "dry run (nothing written)",
            total = hits.Count,
            speakerGenderKnownAndPronounCorrect = result.GenderKnownAndCorrect,
            ofThoseOnlyTheyUsed = result.NeutralForKnownGender,
            summary,
            lines = hits.OrderBy(h => h.Category).ThenBy(h => h.File).ToList(),
        };

        Directory.CreateDirectory($"{workingDirectory}/TestResults");
        FileHelper.WriteAllTextWithRetry($"{workingDirectory}/TestResults/PronounRetranslation.yaml",
            YamlHelper.CreateSerializer().Serialize(report));

        Console.WriteLine($"Pronoun defects: {hits.Count} ({(flagForRetranslation ? "flagged" : "dry run")}); " +
            string.Join(", ", summary.Select(s => $"{s.Key} {s.Value.total}")) +
            ". See TestResults/PronounRetranslation.yaml");

        return result;
    }
}
