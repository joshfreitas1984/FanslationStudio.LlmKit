using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Workflow;

/// <summary>
/// Standard, game-agnostic handling for <see cref="TextFileType.DynamicStringsIL2CPP"/> files -
/// hardcoded, runtime-assembled string literal fragments baked directly into IL2CPP game code
/// (e.g. a String.Concat/String.Format call mixing a Chinese literal like "架势" with data such as
/// a save-slot's task text), discovered by manually inspecting the consuming project's decompiled
/// output (see DragonHeirOverLlm's Converter/README.md and
/// ".github/instructions/dragonheirplugin.instructions.md"'s "dynamic/hardcoded in-code string
/// translation plan") rather than by an automated runtime/offline asset scan like
/// <see cref="PrefabTextWorkflow"/> uses - IL2CPP dummy assemblies have no real IL bodies for a
/// Cecil-based scan to find, so the dump input file here is hand-curated, not machine-generated.
///
/// Mechanically this mirrors <see cref="PrefabTextWorkflow"/> almost exactly (flat list of
/// distinct raw strings -&gt; standard Export/Converted/translate pipeline -&gt; flat raw/result
/// YAML) - kept as a separate type rather than reusing PrefabTextWorkflow directly because the
/// runtime consumption model differs: a PrefabText result is looked up by an exact *whole-string*
/// match against a UI component's full text, whereas a DynamicStringsIL2CPP result is applied as
/// an exact *substring* replacement against a small hardcoded fragment of a larger, otherwise
/// data-driven runtime string (see DragonHeirPlugin/DynamicStringPatches.cs) - keeping the two
/// TextFileType/Workflow pairs distinct avoids conflating those two different semantics even
/// though the underlying Export/Package mechanics are identical.
/// </summary>
public static class DynamicStringWorkflow
{
    /// <summary>
    /// Reads a plain-text file (one distinct hardcoded literal fragment per line, blank lines
    /// ignored) from Raw/Dumped/DynamicStrings/{textFile.Path} and produces the same
    /// TranslationLine YAML shape the CSV/PrefabText export paths use, so it flows through the
    /// existing Converted/merge/translate pipeline unchanged. A literal two-character "\n"
    /// escape sequence in a dumped line - written by the Converter's static-extraction candidate
    /// scan (StringMapExtractor.ExtractDynamicStringCandidates) to represent a real embedded
    /// newline without breaking the "one candidate per line" file format - is unescaped back to a
    /// real newline before decomposing/recording Raw, so the resulting Raw value matches the
    /// actual multi-line string as it's compiled into the game (needed for the runtime substring
    /// match in DragonHeirPlugin/DynamicStringPatches.cs to ever fire) and so
    /// CompoundFieldSplitter.Decompose can treat the newline as the natural fragment boundary it
    /// already recognises (a real "\n" is not in CjkTextChars, so it's never absorbed into a
    /// fragment - each line still gets translated as its own unit via the resulting per-fragment
    /// splits/template).
    /// </summary>
    public static void ExportDynamicStringsToCustomFormat(
        string workingDirectory, TextFileToSplit textFile, CompoundFieldSplitterOptions? options = null)
    {
        var dumpedPath = $"{workingDirectory}/Raw/Dumped/DynamicStrings/{textFile.Path}";
        var exportPath = $"{workingDirectory}/Raw/Export";
        var convertedPath = $"{workingDirectory}/Converted";

        Directory.CreateDirectory(exportPath);
        Directory.CreateDirectory(convertedPath);

        var foundLines = File.ReadAllLines(dumpedPath)
            .Where(dumpedLine => !string.IsNullOrEmpty(dumpedLine))
            .Select(dumpedLine =>
            {
                // Reverse StringMapExtractor.EscapeNewlinesForFlatFile's "\n" escape - see the
                // XML doc above for why this needs to happen before Decompose/Raw are computed.
                var line = dumpedLine.Replace("\\n", "\n");
                var (template, fragments) = CompoundFieldSplitter.Decompose(line, options);

                if (fragments.Count == 0)
                {
                    return new TranslationLine
                    {
                        Raw = line,
                        Splits = [new TranslationSplit(0, 0, line)],
                    };
                }

                if (CompoundFieldSplitter.IsTrivialTemplate(template, fragments.Count))
                {
                    return new TranslationLine
                    {
                        Raw = line,
                        Splits = [new TranslationSplit(0, 0, fragments[0])],
                    };
                }

                return new TranslationLine
                {
                    Raw = line,
                    Templates = [new FieldTemplate(0, template)],
                    Splits = fragments.Select((fragment, index) => new TranslationSplit(0, index, fragment)).ToList(),
                };
            })
            .ToList();

        var serializer = YamlHelper.CreateSerializer();
        var yaml = serializer.Serialize(foundLines);
        File.WriteAllText($"{exportPath}/{textFile.Path}.yaml", yaml);

        if (!File.Exists($"{convertedPath}/{textFile.Path}.yaml"))
            File.Copy($"{exportPath}/{textFile.Path}.yaml", $"{convertedPath}/{textFile.Path}.yaml");
    }

    /// <summary>
    /// Packages a translated DynamicStringsIL2CPP file into the flat raw/result YAML shape a
    /// runtime plugin applies as a global substring-replacement dictionary:
    /// <code>
    /// - raw: 架势
    ///   result: Posture
    /// </code>
    /// Same fallback-to-raw-on-failure semantics as <see cref="PrefabTextWorkflow.PackagePrefabTextAsync"/>.
    /// </summary>
    public static async Task<(int Passed, int Failed)> PackageDynamicStringsAsync(string workingDirectory, TextFileToSplit textFile)
    {
        var outputPath = $"{workingDirectory}/Mod";
        Directory.CreateDirectory(outputPath);

        var results = new List<DynamicStringResult>();
        var seenBareRaw = new HashSet<string>();
        var passedCount = 0;
        var failedCount = 0;

        await FileIteration.IterateTranslatedFilesAsync(workingDirectory, [textFile], async (_, _, fileLines) =>
        {
            foreach (var line in fileLines)
            {
                var (result, failed, bareFragment) = ReconstructLine(line, textFile);
                if (result == null)
                    continue;

                // A raw string still containing a literal "{n}" placeholder (e.g.
                // "{0}年{1}月{2}日") is a String.Format-style template - see
                // DynamicStringResult.IsTemplate for why this must be flagged explicitly rather
                // than left for the runtime consumer to re-derive.
                results.Add(new DynamicStringResult(line.Raw, result, IsFormatTemplate(line.Raw)));

                // Single-fragment templates (e.g. "打扰了;GovernPlotStart;1", split into label
                // "打扰了" + literal ";GovernPlotStart;1") represent game data cells where the
                // trailing literal is action/parameter metadata consumed and stripped off by the
                // game's own parsing before the label ever reaches a TMP_Text/UI.Text component -
                // only the bare label (never the full raw cell) is what actually gets rendered on
                // screen (e.g. an NPC interaction button). The full-compound entry above can only
                // ever match if the entire raw literal is baked into IL2CPP code verbatim and
                // displayed as-is, so it silently never fires for these split-and-consumed cells.
                // Emitting the bare label as its own dictionary entry too lets the runtime
                // substring match fire either way, without needing to know which case applies.
                if (bareFragment is { } fragment && fragment.Raw != fragment.Result
                    && seenBareRaw.Add(fragment.Raw))
                {
                    results.Add(new DynamicStringResult(fragment.Raw, fragment.Result));
                }

                if (failed)
                    failedCount++;
                else
                {
                    passedCount++;
                }
            }

            await Task.CompletedTask;
        });

        var serializer = YamlHelper.CreateSerializer();
        await File.WriteAllTextAsync($"{outputPath}/{textFile.Path}.yaml", serializer.Serialize(results));

        return (passedCount, failedCount);
    }

    // Matches a real String.Format-style placeholder ("{0}", "{12}", ...) OR one of the game's own
    // "#Token#"/"#$Token#" localization markers (e.g. "#TargetInteractName#", "#$PlayerName#") -
    // both are substituted with real data by something other than a literal copy of Raw before a
    // composite string ever reaches DragonHeirPlugin/DynamicStringPatches.cs's Concat/Format
    // postfix or TMP_Text/UI.Text setter, so both need the same structural (regex-shape) matching
    // at runtime rather than a literal substring match against Raw. Deliberately more specific
    // than a bare Raw.Contains('{')/Contains('#') check so a raw fragment that happens to contain
    // an unrelated literal '{' or '#' (e.g. stray markup) is never misclassified as a template.
    // Kept in sync with DynamicStringPatches.cs's PlaceholderOrTokenRegex on the plugin side -
    // CONFIRMED BUG (2026-08-28): a Raw containing ONLY a "#Token#" marker (no "{n}") was never
    // flagged IsTemplate here, so it never reached the plugin's already-correct "#Token#"-aware
    // _compiledTemplates matcher at all - it landed in the plain bare-fragment dictionary instead,
    // where the full raw string (still containing the literal, never-actually-present "#Token#"
    // text) could never match, silently falling through to bare-fragment substring corruption
    // (e.g. "久闻#TargetInteractName#武功高强，不知是否愿意赐教一二。" rendering as
    // "久聞MasterMartial arts高強，不知是否愿意賜教One二0" instead of the correct whole-sentence
    // translation already present in the packaged dictionary).
    private static readonly System.Text.RegularExpressions.Regex FormatPlaceholderRegex = new(
        @"\{\d+\}|#\$?[A-Za-z0-9_]+#", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsFormatTemplate(string raw) => FormatPlaceholderRegex.IsMatch(raw);

    /// <summary>
    /// Reconstructs a single line's packaged output. The returned <c>Failed</c> flag reflects
    /// whether the line fell back to its original raw text because a fragment/split was
    /// unsafe, flagged for retranslation, or missing its translation - this must be reported by
    /// the caller as an actual failure (see <see cref="PackageDynamicStringsAsync"/>) rather than
    /// silently folded into the same bucket as a genuinely successful line, since both cases
    /// otherwise look identical in the packaged raw/result YAML.
    ///
    /// <paramref name="BareFragment"/> is populated only when the line is a single-fragment
    /// template (exactly one translatable split, e.g. "打扰了;GovernPlotStart;1" -> label
    /// "打扰了" + literal ";GovernPlotStart;1") - see the call site in
    /// <see cref="PackageDynamicStringsAsync"/> for why this extra bare label/translation pair
    /// needs to be packaged as its own dictionary entry alongside the full reconstructed line.
    /// </summary>
    private static (string? Result, bool Failed, (string Raw, string Result)? BareFragment) ReconstructLine(TranslationLine line, TextFileToSplit textFile)
    {
        var template = line.Templates.FirstOrDefault(t => t.Split == 0);
        if (template != null)
        {
            var fragments = line.Splits.Where(s => s.Split == 0).OrderBy(s => s.SubIndex).ToList();
            var translatedFragments = new List<string>();

            foreach (var fragment in fragments)
            {
                if (!textFile.PackageOutput || fragment.FlaggedForRetranslation || !fragment.SafeToTranslate)
                    return (line.Raw, true, null);

                if (!string.IsNullOrEmpty(fragment.Translated))
                    translatedFragments.Add(fragment.Translated);
                else if (!string.IsNullOrEmpty(fragment.Text))
                    return (line.Raw, true, null);
                else
                    translatedFragments.Add(fragment.Text);
            }

            var reconstructed = CompoundFieldSplitter.Reconstruct(template.Template, translatedFragments);
            var bareFragment = fragments.Count == 1
                ? (fragments[0].Text, translatedFragments[0])
                : ((string Raw, string Result)?)null;

            return (reconstructed, false, bareFragment);
        }

        var split = line.Splits.FirstOrDefault(s => s.Split == 0);
        if (split == null)
            return (null, false, null);

        if (!string.IsNullOrEmpty(split.Translated) && !split.FlaggedForRetranslation && split.SafeToTranslate)
            return (split.Translated, false, null);

        return (split.Text, true, null);
    }
}
