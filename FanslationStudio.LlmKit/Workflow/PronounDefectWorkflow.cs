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
    /// <summary>
    /// <paramref name="SkipWhenTranslationNamesSomeone"/> leaves out an invented-gender hit whose translation names a
    /// character. Use <see cref="Prose"/> for a game whose text is running prose rather than stage directions: there a
    /// pronoun after a named character is usually right, and a single line cannot show otherwise.
    /// </summary>
    public sealed record PronounDefectOptions(bool SkipWhenTranslationNamesSomeone = false, IReadOnlyCollection<string>? IgnoreSources = null)
    {
        public static PronounDefectOptions Prose { get; } = new(SkipWhenTranslationNamesSomeone: true);

        /// <summary>The options Config.yaml's <c>pronounCheck</c> section asks for.</summary>
        public static PronounDefectOptions From(PronounCheckConfig config) => new(config.SkipWhenTranslationNamesSomeone, config.IgnoreSources);
    }

    /// <summary>Config.yaml's <c>pronounCheck</c> section, or the defaults when the working directory has no Config.yaml.</summary>
    private static PronounCheckConfig LoadPronounCheck(string workingDirectory, GameHooks? hooks) =>
        File.Exists($"{workingDirectory}/Config.yaml")
            ? ConfigurationExtensions.GetConfiguration(workingDirectory, hooks).PronounCheck
            : new PronounCheckConfig();

    /// <summary>What <see cref="Classify"/> decided about one translated split.</summary>
    public sealed record PronounClassification(string? Category, bool GenderKnownAndCorrect, bool OnlyNeutralForKnownGender);

    /// <summary>
    /// Decides whether <paramref name="split"/>'s current translation has a pronoun defect, given the line context
    /// the game supplies for it (if any). Shared by <see cref="FindAsync"/> and the rules pass.
    /// </summary>
    public static PronounClassification Classify(TranslationSplit split, GameHooks? hooks, LineContext? context, bool skipWhenNamesSomeone)
    {
        var tokens = hooks?.UnknownGenderPersonTokens;
        var known = context is { GenderKnown: true } ? context : null;

        if (known == null)
        {
            var category = LineValidation.InventsGender(split.Text, split.Translated, skipWhenNamesSomeone, tokens) ? "InventedGender"
                : LineValidation.NarratesAsFirstPerson(split.Text, split.Translated) ? "NarratedAsFirstPerson"
                : null;
            return new PronounClassification(category, false, false);
        }

        // The speaker's gender is known, so he/she is allowed - but only if it is the right one.
        // A continuation split that opens with he/she points back to the previous sentence's subject, which it cannot see.
        var pointsBack = split.SubIndex > 0 && LineValidation.OpensWithPronounSubject(split.Translated);
        var knownCategory = !pointsBack && LineValidation.ContradictsGender(split.Text, split.Translated, known.Gender) ? "WrongGender"
            : !pointsBack && LineValidation.ContradictsGenderDespiteKinshipTerm(split.Text, split.Translated, known.Gender) ? "WrongGenderKinshipTerm"
            : LineValidation.NarratesAsFirstPerson(split.Text, split.Translated) ? "NarratedAsFirstPerson"
            : null;

        var correct = knownCategory == null && LineValidation.InventsGender(split.Text, split.Translated, skipWhenNamesSomeone, tokens);
        return new PronounClassification(knownCategory, correct, correct && LineValidation.UsesOnlyNeutralPronouns(split.Translated));
    }
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
        TextFileToSplit[] textFiles, bool flagForRetranslation, GameHooks? hooks = null, PronounDefectOptions? options = null)
    {
        options ??= PronounDefectOptions.From(LoadPronounCheck(workingDirectory, hooks));
        var skipNamed = options.SkipWhenTranslationNamesSomeone;
        var ignored = options.IgnoreSources?.ToHashSet(StringComparer.Ordinal) ?? [];
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
                    if (split.FlaggedForRetranslation || split.Text.Length == 0 || split.Translated.Length == 0 || ignored.Contains(split.Text))
                        continue;

                    LineContext? context = null;
                    contexts?.TryGetValue(split, out context);

                    var classification = Classify(split, hooks, context, skipNamed);
                    if (classification.GenderKnownAndCorrect)
                    {
                        Interlocked.Increment(ref genderKnownAndCorrect);
                        if (classification.OnlyNeutralForKnownGender)
                            Interlocked.Increment(ref neutralForKnownGender);
                    }

                    var category = classification.Category;
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
        bool flagForRetranslation = false, GameHooks? hooks = null, PronounDefectOptions? options = null)
    {
        var result = await FindAsync(workingDirectory, textFiles, flagForRetranslation, hooks, options);
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
