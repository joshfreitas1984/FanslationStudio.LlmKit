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
        var response = deserializer.Deserialize<LlmConfig>(File.ReadAllText($"{workingDirectory}/Config.yaml", Encoding.UTF8));

        response.Hooks = hooks ?? new GameHooks();

        // Validate models
        if (response.Models == null || response.Models.Count == 0)
            throw new InvalidOperationException("At least one model configuration must be provided in Config.yaml.");

        // YamlDotNet deserializes keys with no items (e.g. all entries commented out) as null,
        // overriding the property's default empty-list initializer. Guard against that here.
        response.SplitRegexPatterns ??= new List<string>();
        response.SplitCharactersList ??= new List<string>();
        response.ExtraStringTokenReplacers ??= new List<string>();
        // Same guard as above for a `qualityReview:` key present with no content underneath it
        // (e.g. every field commented out) - the property initializer only covers a fully absent
        // key, not one present but empty.
        response.QualityReview ??= new QualityReviewConfig();

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
                ModelParams = model.ModelParams,
                Prompts = new Dictionary<string, string>()
            };

            // Load Presets - add more here
            if (model.ModelPreset == ModelPreset.Qwen25)
                runtimeConfig = MergeModelConfig(GetQwen25Preset(deserializer, model), runtimeConfig);
            else if (model.ModelPreset == ModelPreset.Qwen38)
                runtimeConfig = MergeModelConfig(GetQwen38Preset(deserializer, model), runtimeConfig);
            else if (model.ModelPreset == ModelPreset.Glm4)
                runtimeConfig = MergeModelConfig(GetGlm4Preset(deserializer, model), runtimeConfig);
      

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

        // Same fail-fast treatment for the quality review pass's model name (see
        // QualityReviewConfig.ModelName doc comment) - only checked when QC is actually enabled,
        // so a project that never opts in never needs a qualityReview: section at all.
        if (response.QualityReview.Enabled
            && !string.IsNullOrEmpty(response.QualityReview.ModelName)
            && !response.Runtime.Models.ContainsKey(response.QualityReview.ModelName))
            throw new InvalidOperationException(
                $"QualityReview.ModelName '{response.QualityReview.ModelName}' does not match any configured model name. " +
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

    private static ModelExecutionConfig GetQwen25Preset(IDeserializer deserializer, ModelConfig model) =>
        GetPresetModelConfig(ModelPreset.Qwen25, model.ModelPresetType);

    private static ModelExecutionConfig GetQwen38Preset(IDeserializer deserializer, ModelConfig model) =>
        GetPresetModelConfig(ModelPreset.Qwen38, model.ModelPresetType);

    /// <summary>
    /// GLM-4 preset - ships the full Qwen25-equivalent prompt set (BaseSystemPrompt, Corrections,
    /// Dynamics) under BaseFiles/Glm4/ so it can be used as a drop-in swap. Its
    /// BaseQualityReviewPrompt is its own tuned wording (see the comment in Config.yaml on
    /// `models:`); the rest were copied verbatim from Qwen25 and not yet re-tuned for GLM-4.
    /// </summary>
    private static ModelExecutionConfig GetGlm4Preset(IDeserializer deserializer, ModelConfig model) =>
        GetPresetModelConfig(ModelPreset.Glm4, model.ModelPresetType);

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
            ModelPreset.Glm4 => "Glm4",
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
            ModelParams = presetType == ModelPresetType.Standard ?
                presetConfig.ModelParams
                : presetConfig.StructuredTextModelParams,
            Prompts = LoadPresetPrompts($"FanslationStudio.LlmKit.BaseFiles.{presetName}")
        };
    }

    public static Dictionary<string, string> LoadPresetPrompts(string resourcePrefix)
    {
        var assembly = typeof(ConfigurationExtensions).Assembly;
        var prompts = new Dictionary<string, string>();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePrefix) && name.EndsWith(".txt"));

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

    public static void MergeWorkspacePrompts(string promptsDirectory, ModelExecutionConfig config)
    {
        if (!Directory.Exists(promptsDirectory))
            return;

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

        foreach (var file in Directory.EnumerateFiles(glossaryDirectory))
        {
            var newLines = deserializer.Deserialize<List<GlossaryLine>>(File.ReadAllText(file, Encoding.UTF8)) ?? [];
            foreach (var newLine in newLines)
            {
                var existing = executionValues.GlossaryLines.FirstOrDefault(l =>
                    (!string.IsNullOrEmpty(newLine.Raw) && l.Raw == newLine.Raw) ||
                    (!string.IsNullOrEmpty(newLine.RawSimplified) && l.RawSimplified == newLine.RawSimplified) ||
                    (!string.IsNullOrEmpty(newLine.RawTraditional) && l.RawTraditional == newLine.RawTraditional));

                if (existing != null)
                    executionValues.GlossaryLines[executionValues.GlossaryLines.IndexOf(existing)] = newLine;
                else
                    executionValues.GlossaryLines.Add(newLine);
            }
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

        // Override all model parameters if any are provided in the override config
        if (overrideConfig.ModelParams != null)
            baseConfig.ModelParams = overrideConfig.ModelParams;

        return baseConfig;
    }
}
