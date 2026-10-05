using FanslationStudio.LlmKit.Utility;

namespace FanslationStudio.LlmKit.Support;

/// <summary>One <c>Converted/*.yaml</c> file held in memory for the length of a run.</summary>
internal sealed class CorpusFile
{
    public required TextFileToSplit TextFile { get; init; }
    public required string OutputFile { get; init; }
    public required List<TranslationLine> Lines { get; init; }
}

/// <summary>
/// The working set of <c>Converted/*.yaml</c> files (plus <c>TestResults/OldFiles</c>), loaded once
/// and shared across passes - lets a multi-pass loop (translate, re-apply rules, translate again)
/// reuse the deserialized lines instead of re-reading and re-parsing the whole corpus each pass.
/// Callers that mutate lines are responsible for writing the files they changed.
/// </summary>
internal sealed class TranslationCorpus
{
    private readonly string _workingDirectory;
    private readonly TextFileToSplit[] _textFiles;
    private readonly CorpusFile?[] _files;
    private List<TranslationLine>? _oldFileLines;

    private TranslationCorpus(string workingDirectory, TextFileToSplit[] textFiles)
    {
        _workingDirectory = workingDirectory;
        _textFiles = textFiles;
        _files = new CorpusFile?[textFiles.Length];
    }

    public string WorkingDirectory => _workingDirectory;

    public string ConvertedDirectory => $"{_workingDirectory}/Converted";

    /// <summary>Loaded files, in <see cref="TextFileToSplit"/> order.</summary>
    public IEnumerable<CorpusFile> Files => _files.OfType<CorpusFile>();

    /// <summary>
    /// Loads every file that already exists in <c>Converted</c>. With <paramref name="copyMissingFromExport"/>,
    /// a file missing there is first seeded from <c>Raw/Export</c> (the translation schedulers' behavior).
    /// </summary>
    public static TranslationCorpus Load(string workingDirectory, TextFileToSplit[] textFiles, bool copyMissingFromExport)
    {
        var corpus = new TranslationCorpus(workingDirectory, textFiles);
        corpus.LoadMissing(copyMissingFromExport);
        return corpus;
    }

    /// <summary>Loads any file not loaded yet - see <see cref="Load"/>.</summary>
    public void LoadMissing(bool copyMissingFromExport)
    {
        if (copyMissingFromExport && !Directory.Exists(ConvertedDirectory))
            Directory.CreateDirectory(ConvertedDirectory);

        var deserializer = YamlHelper.CreateDeserializer();

        for (var i = 0; i < _textFiles.Length; i++)
        {
            if (_files[i] != null)
                continue;

            var textFile = _textFiles[i];
            var outputFile = $"{ConvertedDirectory}/{textFile.Path}.yaml";

            if (!File.Exists(outputFile))
            {
                if (!copyMissingFromExport)
                    continue;

                File.Copy($"{_workingDirectory}/Raw/Export/{textFile.Path}", outputFile);
            }

            _files[i] = new CorpusFile
            {
                TextFile = textFile,
                OutputFile = outputFile,
                Lines = deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(outputFile)),
            };
        }
    }

    /// <summary>Every line from <c>TestResults/OldFiles</c>, parsed on first use.</summary>
    public List<TranslationLine> OldFileLines
    {
        get
        {
            if (_oldFileLines != null)
                return _oldFileLines;

            var deserializer = YamlHelper.CreateDeserializer();
            _oldFileLines = [];

            foreach (var file in Directory.EnumerateFiles($"{_workingDirectory}/TestResults/OldFiles"))
                _oldFileLines.AddRange(deserializer.Deserialize<List<TranslationLine>>(File.ReadAllText(file)));

            return _oldFileLines;
        }
    }
}
