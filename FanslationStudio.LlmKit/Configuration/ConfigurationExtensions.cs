using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using System.Text;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Configuration;

public static class ConfigurationExtensions
{
    public static LlmConfig GetConfiguration(string workingDirectory, GameHooks? hooks = null)
    {
        var deserializer = YamlHelper.CreateDeserializer();
        var configText = File.ReadAllText($"{workingDirectory}/Config.yaml", Encoding.UTF8);
        var response = deserializer.Deserialize<LlmConfig>(configText);

        response.Hooks = hooks ?? new GameHooks();

        // Validate models
        if (response.Models == null || response.Models.Count == 0)
            throw new InvalidOperationException("At least one model configuration must be provided in Config.yaml.");

        // YamlDotNet deserializes keys with no items (e.g. all entries commented out) as null,
        // overriding the property's default empty-list initializer. Guard against that here.
        response.SplitRegexPatterns ??= new List<string>();
        response.SplitCharactersList ??= new List<string>();
        response.ExtraStringTokenReplacers ??= new List<string>();
        // Same guard as above for a `qualityControl:` key present with no content underneath it
        // (e.g. every field commented out) - the property initializer only covers a fully absent
        // key, not one present but empty.
        response.QualityControl ??= new QualityControlConfig();

        // Legacy `qualityReview:` key (renamed `qualityControl:`): honoured when the new key is absent.
        if (response.LegacyQualityReview != null)
        {
            WarnLegacy("Config.yaml key 'qualityReview:' is deprecated; rename it to 'qualityControl:'.");
            if (!HasTopLevelKey(configText, "qualityControl"))
                response.QualityControl = response.LegacyQualityReview;
        }

        response.Runtime.WorkingDirectory = workingDirectory;

        // Load Manual Translations if exists
        var manualTranslationsFile = $"{workingDirectory}/ManualTranslations.yaml";
        if (File.Exists(manualTranslationsFile))
            response.Runtime.ManualTranslations =
                deserializer.Deserialize<List<GlossaryLine>>(File.ReadAllText(manualTranslationsFile, Encoding.UTF8))
                ?? new List<GlossaryLine>();


        // Load and Merge Model Presets
        foreach (var model in response.Models)
        {
            if (model.Name == string.Empty)
                model.Name = "Standard";

            if (response.Runtime.Models.ContainsKey(model.Name))
                throw new InvalidOperationException($"Duplicate model name '{model.Name}' found in configuration.");

            var runtimeConfig = new ModelExecutionConfig
            {
                Model = model.Model,
                Url = model.Url,
                ApiKeyRequired = model.ApiKeyRequired,
                EnableThinking = model.EnableThinking,
                ModelParams = model.ModelParams,
                Prompts = new Dictionary<string, string>()
            };

            if (model.ModelPreset != ModelPreset.None)
                runtimeConfig = MergeModelConfig(GetPresetModelConfig(model.ModelPreset, model.ModelPresetType), runtimeConfig);


            // Set the merged config to runtime
            response.Runtime.Models[model.Name] = runtimeConfig;

            // Merge custom prompts if they are specified
            var customPromptsPath = string.IsNullOrEmpty(model.CustomPromptsPath) ?
                $"{workingDirectory}/{model.Name}Prompts"
                : $"{workingDirectory}/{model.CustomPromptsPath}";

            MergeWorkspacePrompts(customPromptsPath, response.Runtime.Models[model.Name]);

            var apiKeyPath = model.ApiKeyFilePath == string.Empty ?
                $"{workingDirectory}/{model.Name}ApiKey.txt"
                : $"{workingDirectory}/{model.ApiKeyFilePath}";

            LoadApiKey(apiKeyPath, response.Runtime.Models[model.Name]);
        }

        // Fail fast on a typo'd/unconfigured escalation model name rather than silently never
        // escalating (see LlmConfig.EscalationModelName doc comment).
        if (!string.IsNullOrEmpty(response.EscalationModelName)
            && !response.Runtime.Models.ContainsKey(response.EscalationModelName))
            throw new InvalidOperationException(
                $"EscalationModelName '{response.EscalationModelName}' does not match any configured model name. " +
                $"Configured model names: {string.Join(", ", response.Runtime.Models.Keys)}");

        // Same fail-fast treatment for the quality control pass's model name (see
        // QualityControlConfig.ModelName doc comment) - only checked when QC is actually enabled,
        // so a project that never opts in never needs a qualityControl: section at all.
        if (response.QualityControl.Enabled
            && !string.IsNullOrEmpty(response.QualityControl.ModelName)
            && !response.Runtime.Models.ContainsKey(response.QualityControl.ModelName))
            throw new InvalidOperationException(
                $"QualityControl.ModelName '{response.QualityControl.ModelName}' does not match any configured model name. " +
                $"Configured model names: {string.Join(", ", response.Runtime.Models.Keys)}");

        // Load Preset Glossary before workspace glossary so that workspace can override preset entries
        LoadPresetGlossary(deserializer, response);
        MergeWorkspaceGlossary($"{workingDirectory}/Glossary", deserializer, response.Runtime);

        // Change hyphens to non-breaking hyphens to avoid Unity line-breaking them when rendering
        foreach (var line in response.Runtime.GlossaryLines)
            line.Result = line.Result.Replace("-", "\u2011");

        foreach (var line in response.Runtime.ManualTranslations)
            line.Result = line.Result.Replace("-", "\u2011");

        StringTokenReplacer.SetExtraTokens(response.ExtraStringTokenReplacers);

        return response;
    }

    private static void LoadApiKey(string apiKeyFile, ModelExecutionConfig config)
    {
        if (File.Exists(apiKeyFile))
            config.ApiKey = File.ReadAllText(apiKeyFile, Encoding.UTF8).Trim();
        else if (config.ApiKeyRequired ?? false)
            throw new InvalidOperationException($"API key is required but '{apiKeyFile}' not found.");
    }

    /// <summary>
    /// Public entry point onto a preset's embedded Model/Url/ModelParams/Prompts (the same data
    /// <see cref="GetConfiguration"/> merges into a workspace's Models dictionary), without
    /// requiring a full workspace Config.yaml. Used by <see cref="Workflow.PromptOptimisationWorkflow"/>
    /// to call a preset's real configured model (its embedded BaseFiles/&lt;preset&gt;/Config.yaml
    /// - a real Ollama URL, not a placeholder) directly.
    /// </summary>
    public static ModelExecutionConfig GetPresetModelConfig(ModelPreset preset, ModelPresetType presetType)
    {
        var presetName = preset switch
        {
            ModelPreset.Qwen25 => "Qwen25",
            ModelPreset.Qwen38 => "Qwen38",
            ModelPreset.HyMT2 => "HyMT2",
            ModelPreset.HyMT2Moe => "HyMT2Moe",
            _ => throw new InvalidOperationException($"No preset configuration available for '{preset}'."),
        };

        var deserializer = YamlHelper.CreateDeserializer();
        var assembly = typeof(ConfigurationExtensions).Assembly;
        var resourceName = $"FanslationStudio.LlmKit.BaseFiles.{presetName}.Config.yaml";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var presetConfig = deserializer.Deserialize<PresetConfig>(reader);

        return new ModelExecutionConfig
        {
            Model = presetConfig.Model,
            Url = presetConfig.Url,
            ApiKeyRequired = presetConfig.ApiKeyRequired,
            EnableThinking = presetConfig.EnableThinking,
            ModelParams = presetType == ModelPresetType.Standard ?
                presetConfig.ModelParams
                : presetConfig.StructuredTextModelParams,
            Prompts = LoadPresetPromptsWithCommon(presetName)
        };
    }

    /// <summary>
    /// Loads BaseFiles/Common - the QC, correction and dynamic-tag prompts shared verbatim by two or
    /// more presets (currently Qwen38/HyMT2/HyMT2Moe's whole shared set, plus a couple of files every
    /// preset agrees on) - and then overlays the given preset's own BaseFiles/&lt;preset&gt; files on
    /// top by filename. A preset only needs to ship the files where its wording genuinely diverges
    /// (e.g. Qwen25's own system/QC/correction wording, or HyMT2Moe's own dynamic-tag prompts);
    /// everything else falls through to Common.
    /// </summary>
    private static Dictionary<string, string> LoadPresetPromptsWithCommon(string presetName)
    {
        var prompts = LoadPresetPrompts("FanslationStudio.LlmKit.BaseFiles.Common");

        foreach (var (key, value) in LoadPresetPrompts($"FanslationStudio.LlmKit.BaseFiles.{presetName}"))
            prompts[key] = value;

        return prompts;
    }

    public static Dictionary<string, string> LoadPresetPrompts(string resourcePrefix)
    {
        var assembly = typeof(ConfigurationExtensions).Assembly;
        var prompts = new Dictionary<string, string>();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePrefix + ".", StringComparison.Ordinal)
                && name.EndsWith(".txt", StringComparison.Ordinal));

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var promptContent = reader.ReadToEnd();
            var promptKey = Path.GetFileNameWithoutExtension(resourceName).Split('.').Last();

            prompts.Add(promptKey, promptContent);
        }
        return prompts;
    }

    private static readonly HashSet<string> WarnedLegacy = new();

    private static void WarnLegacy(string message)
    {
        lock (WarnedLegacy)
            if (!WarnedLegacy.Add(message))
                return;
        Console.Error.WriteLine("WARNING: " + message);
    }

    private static bool HasTopLevelKey(string yaml, string key) =>
        System.Text.RegularExpressions.Regex.IsMatch(yaml, $@"^{key}\s*:", System.Text.RegularExpressions.RegexOptions.Multiline);

    public static void MergeWorkspacePrompts(string promptsDirectory, ModelExecutionConfig config)
    {
        if (!Directory.Exists(promptsDirectory))
            return;

        foreach (var legacy in Directory.EnumerateFiles(promptsDirectory, "BaseQualityReview*"))
            WarnLegacy($"Prompt override '{Path.GetFileName(legacy)}' uses a legacy name and no longer applies; rename it to 'BaseQualityControl*'.");

        // Merge with existing prompts, allowing workspace prompts to override preset prompts
        foreach (var file in Directory.EnumerateFiles(promptsDirectory))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            var value = File.ReadAllText(file, Encoding.UTF8);
            config.Prompts[key] = value;
        }
    }

    private static void LoadPresetGlossary(IDeserializer deserializer, LlmConfig response)
    {
        if (!response.GlossaryPreset.UsePresetChineseGlossary)
            return;

        var assembly = typeof(ConfigurationExtensions).Assembly;
        var presetGlossaryLines = new List<GlossaryLine>();

        var glossaryTypesToLoad = Enum.GetValues<ChineseGlossaryTypes>()
            .Except(response.GlossaryPreset.ChineseGlossaryTypesToSupress);

        foreach (var glossaryType in glossaryTypesToLoad)
        {
            var resourceName = $"FanslationStudio.LlmKit.BaseFiles.ChineseGlossary.{glossaryType}.yaml";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = deserializer.Deserialize<List<GlossaryLine>>(reader) ?? [];
            presetGlossaryLines.AddRange(lines);
        }

        response.Runtime.GlossaryLines = presetGlossaryLines;
    }

    private static void MergeWorkspaceGlossary(string glossaryDirectory, IDeserializer deserializer, RuntimeValues executionValues)
    {
        if (!Directory.Exists(glossaryDirectory))
            return;

        var index = new GlossaryIndex(executionValues.GlossaryLines);

        foreach (var file in Directory.EnumerateFiles(glossaryDirectory))
        {
            var newLines = deserializer.Deserialize<List<GlossaryLine>>(File.ReadAllText(file, Encoding.UTF8)) ?? [];
            foreach (var newLine in newLines)
                index.ReplaceOrAdd(newLine);
        }
    }

    /// <summary>
    /// Keyed view over a glossary list for workspace overrides: a new entry replaces the earliest
    /// existing entry sharing its Raw, RawSimplified or RawTraditional (non-empty keys only), or is
    /// appended. Same result as a FirstOrDefault scan per entry, without the O(n*m) cost.
    /// </summary>
    internal sealed class GlossaryIndex
    {
        private readonly List<GlossaryLine> _lines;
        private readonly Dictionary<string, SortedSet<int>> _byRaw = [];
        private readonly Dictionary<string, SortedSet<int>> _bySimplified = [];
        private readonly Dictionary<string, SortedSet<int>> _byTraditional = [];

        public GlossaryIndex(List<GlossaryLine> lines)
        {
            _lines = lines;
            for (var i = 0; i < lines.Count; i++)
                Track(i, lines[i]);
        }

        public void ReplaceOrAdd(GlossaryLine newLine)
        {
            var existing = Math.Min(First(_byRaw, newLine.Raw),
                Math.Min(First(_bySimplified, newLine.RawSimplified), First(_byTraditional, newLine.RawTraditional)));

            if (existing == int.MaxValue)
            {
                _lines.Add(newLine);
                Track(_lines.Count - 1, newLine);
                return;
            }

            Untrack(existing, _lines[existing]);
            _lines[existing] = newLine;
            Track(existing, newLine);
        }

        private static int First(Dictionary<string, SortedSet<int>> map, string key) =>
            !string.IsNullOrEmpty(key) && map.TryGetValue(key, out var set) && set.Count > 0 ? set.Min : int.MaxValue;

        private void Track(int i, GlossaryLine line)
        {
            Add(_byRaw, line.Raw, i);
            Add(_bySimplified, line.RawSimplified, i);
            Add(_byTraditional, line.RawTraditional, i);
        }

        private void Untrack(int i, GlossaryLine line)
        {
            if (line.Raw != null && _byRaw.TryGetValue(line.Raw, out var a)) a.Remove(i);
            if (line.RawSimplified != null && _bySimplified.TryGetValue(line.RawSimplified, out var b)) b.Remove(i);
            if (line.RawTraditional != null && _byTraditional.TryGetValue(line.RawTraditional, out var c)) c.Remove(i);
        }

        private static void Add(Dictionary<string, SortedSet<int>> map, string? key, int i)
        {
            if (key == null)
                return;

            if (!map.TryGetValue(key, out var set))
                map[key] = set = [];

            set.Add(i);
        }
    }

    public static ModelExecutionConfig MergeModelConfig(ModelExecutionConfig baseConfig, ModelExecutionConfig? overrideConfig)
    {
        if (overrideConfig == null)
            return baseConfig;

        if (!string.IsNullOrEmpty(overrideConfig.Model))
            baseConfig.Model = overrideConfig.Model;

        if (!string.IsNullOrEmpty(overrideConfig.Url))
            baseConfig.Url = overrideConfig.Url;

        if (overrideConfig.ApiKeyRequired ?? false)
            baseConfig.ApiKeyRequired = true;

        if (overrideConfig.EnableThinking.HasValue)
            baseConfig.EnableThinking = overrideConfig.EnableThinking;

        // Override all model parameters if any are provided in the override config
        if (overrideConfig.ModelParams != null)
            baseConfig.ModelParams = overrideConfig.ModelParams;

        return baseConfig;
    }
}
