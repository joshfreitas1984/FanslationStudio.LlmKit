using System.Text;
using FanslationStudio.LlmKit.Support;
using FanslationStudio.LlmKit.Utility;
using YamlDotNet.Serialization;

namespace FanslationStudio.LlmKit.Configuration;

public static class ConfigurationExtensions
{
    public static LlmConfig GetConfiguration(string workingDirectory)
    {
        var deserializer = YamlHelper.CreateDeserializer();
        var response = deserializer.Deserialize<LlmConfig>(File.ReadAllText($"{workingDirectory}/Config.yaml", Encoding.UTF8));

        response.ExecutionValues.WorkingDirectory = workingDirectory;

        // Load Manual Translations if exists
        var manualTranslationsFile = $"{workingDirectory}/ManualTranslations.yaml";
        if (File.Exists(manualTranslationsFile))
            response.ExecutionValues.ManualTranslations =
                deserializer.Deserialize<List<GlossaryLine>>(File.ReadAllText(manualTranslationsFile, Encoding.UTF8))
                ?? new List<GlossaryLine>();

        // Load and Merge Model Presets
        if (response.Presets.StructuredTextModelPreset == ModelPreset.Qwen25
            || response.Presets.StandardModelPreset == ModelPreset.Qwen25)
            LoadQwen25Preset(deserializer, response);

        MergeWorkspacePrompts($"{workingDirectory}/StandardPrompts", true, response.ExecutionValues);
        MergeWorkspacePrompts($"{workingDirectory}/StructuredPrompts", false, response.ExecutionValues);

        // Merge Model Configuration
        MergeModelConfig(response.ExecutionValues.StandardModel, response.WorkspaceStandardModel);
        MergeModelConfig(response.ExecutionValues.StructuredTextModel, response.WorkspaceStructuredTextModel);

        // Load API Keys
        LoadApiKey($"{workingDirectory}/StandardApiKey.txt", response.ExecutionValues.StandardModel);
        LoadApiKey($"{workingDirectory}/StructuredTextApiKey.txt", response.ExecutionValues.StructuredTextModel);

        // Load Preset Glossary before workspace glossary so that workspace can override preset entries
        LoadPresetGlossary(deserializer, response);
        MergeWorkspaceGlossary($"{workingDirectory}/Glossary", deserializer, response.ExecutionValues);

        // Change hyphens to non-breaking hyphens to avoid Unity line-breaking them when rendering
        foreach (var line in response.ExecutionValues.GlossaryLines)
            line.Result = line.Result.Replace("-", "\u2011");

        foreach (var line in response.ExecutionValues.ManualTranslations)
            line.Result = line.Result.Replace("-", "\u2011");

        return response;
    }

    private static void LoadApiKey(string apiKeyFile, ModelExecutionConfig config)
    {
        if (File.Exists(apiKeyFile))
            config.ApiKey = File.ReadAllText(apiKeyFile, Encoding.UTF8).Trim();
        else if (config.ApiKeyRequired ?? false)
            throw new InvalidOperationException($"API key is required but '{apiKeyFile}' not found.");
    }

    private static void LoadQwen25Preset(IDeserializer deserializer, LlmConfig config)
    {
        var assembly = typeof(ConfigurationExtensions).Assembly;
        var resourceName = "FanslationStudio.LlmKit.BaseFiles.Qwen25.Config.yaml";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var presetConfig = deserializer.Deserialize<PresetModelConfig>(reader);

        if (config.Presets.StandardModelPreset == ModelPreset.Qwen25)
        {
            config.ExecutionValues.StandardModel.Model = presetConfig.Model;
            config.ExecutionValues.StandardModel.Url = presetConfig.Url;
            config.ExecutionValues.StandardModel.ApiKeyRequired = presetConfig.ApiKeyRequired;
            config.ExecutionValues.StandardModel.ModelParams = presetConfig.ModelParams;
            config.ExecutionValues.StandardModel.Prompts = LoadPresetPrompts("FanslationStudio.LlmKit.BaseFiles.Qwen25");
        }

        if (config.Presets.StructuredTextModelPreset == ModelPreset.Qwen25)
        {
            config.ExecutionValues.StructuredTextModel.Model = presetConfig.Model;
            config.ExecutionValues.StructuredTextModel.Url = presetConfig.Url;
            config.ExecutionValues.StructuredTextModel.ApiKeyRequired = presetConfig.ApiKeyRequired;
            config.ExecutionValues.StructuredTextModel.Model = presetConfig.Model;
            config.ExecutionValues.StructuredTextModel.ModelParams = presetConfig.ModelParams;
            config.ExecutionValues.StructuredTextModel.Prompts = LoadPresetPrompts("FanslationStudio.LlmKit.BaseFiles.Qwen25");
        }
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
            var promptKey = Path.GetFileNameWithoutExtension(resourceName);

            prompts.Add(promptKey, promptContent);
        }
        return prompts;
    }

    public static void MergeWorkspacePrompts(string promptsDirectory, bool isStandard, ExecutionValues executionValues)
    {
        if (!Directory.Exists(promptsDirectory))
            return;

        // Merge with existing prompts, allowing workspace prompts to override preset prompts
        foreach (var file in Directory.EnumerateFiles(promptsDirectory))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            var value = File.ReadAllText(file, Encoding.UTF8);

            if (isStandard)
                executionValues.StandardModel.Prompts[key] = value;
            else
                executionValues.StructuredTextModel.Prompts[key] = value;
        }
    }

    private static void LoadPresetGlossary(IDeserializer deserializer, LlmConfig response)
    {
        if (!response.Presets.UsePresetChineseGlossary)
            return;

        var assembly = typeof(ConfigurationExtensions).Assembly;
        var presetGlossaryLines = new List<GlossaryLine>();

        var glossaryTypesToLoad = Enum.GetValues<ChineseGlossaryTypes>()
            .Except(response.Presets.ChineseGlossaryTypesToSupress);

        foreach (var glossaryType in glossaryTypesToLoad)
        {
            var resourceName = $"FanslationStudio.LlmKit.BaseFiles.ChineseGlossary.{glossaryType}.yaml";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = deserializer.Deserialize<List<GlossaryLine>>(reader) ?? [];
            presetGlossaryLines.AddRange(lines);
        }

        response.ExecutionValues.GlossaryLines = presetGlossaryLines;
    }

    private static void MergeWorkspaceGlossary(string glossaryDirectory, IDeserializer deserializer, ExecutionValues executionValues)
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
    public static void MergeModelConfig(ModelUrlConfig baseConfig, ModelUrlConfig? overrideConfig)
    {
        if (overrideConfig == null)
            return;

        if (!string.IsNullOrEmpty(overrideConfig.Model))
            baseConfig.Model = overrideConfig.Model;

        if (!string.IsNullOrEmpty(overrideConfig.Url))
            baseConfig.Url = overrideConfig.Url;

        if (overrideConfig.ApiKeyRequired ?? false)
            baseConfig.ApiKeyRequired = true;

        // Override all model parameters if any are provided in the override config
        if (overrideConfig.ModelParams != null)
            baseConfig.ModelParams = overrideConfig.ModelParams;
    }
}
