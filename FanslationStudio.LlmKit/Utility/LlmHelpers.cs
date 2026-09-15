using FanslationStudio.LlmKit.Configuration;
using System.Dynamic;
using System.Text.Json;

namespace FanslationStudio.LlmKit.Utility;

public static class LlmHelpers
{
    public static object GenerateSystemPrompt(string? systemPrompt)
    {
        return new { role = "system", content = systemPrompt };
    }

    public static object GenerateUserPrompt(string? text)
    {
        return new { role = "user", content = text };
    }

    public static object GenerateAssistantPrompt(string? text)
    {
        return new { role = "assistant", content = text };
    }

    public static ModelExecutionConfig CalculateModelConfig(LlmConfig config, string preparedRaw)
    {
        // TODO: Implement properly
        //return preparedRaw.Contains("<")
        //    ? config.Runtime.Models.First().Value
        //    : config.Runtime.Models.Last().Value;

        return config.Runtime.Models.First().Value;
    }

    public static string GenerateLlmRequestData(ModelExecutionConfig modelConfig, List<object> messages)
    {
        if (modelConfig.ModelParams != null)
        {
            // Create a dynamic object and populate it with Params
            dynamic requestBody = new ExpandoObject();
            requestBody.model = modelConfig.Model;
            requestBody.stream = false;
            requestBody.think = false;
            requestBody.messages = messages;

            // Ollama's native /api/chat (as opposed to an OpenAI-compatible /v1/chat/completions
            // endpoint, the other branch below) requires every generation parameter - num_ctx,
            // temperature, top_p, top_k, repeat_penalty, num_predict, frequency_penalty,
            // presence_penalty - nested under a single "options" object, NOT flattened onto the
            // request root. A top-level "num_ctx" is silently ignored by Ollama's server (unknown
            // field), so the request always ran on Ollama's own built-in default context size
            // regardless of what Config.yaml's modelParams.num_ctx said - the exact cause of a
            // "request (N tokens) exceeds the available context size (2048 tokens)" error even
            // after num_ctx was raised in config.
            var options = new Dictionary<string, object>();
            foreach (var param in modelConfig.ModelParams)
                if (decimal.TryParse(param.Value.ToString(), out var param2))
                    options[param.Key] = param2;
                else
                    options[param.Key] = param.Value;
            requestBody.options = options;

            return JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var requestBody = new
            {
                model = modelConfig.Model,
                temperature = 0.1,
                max_tokens = 1000,
                top_p = 1.0,
                top_k = 20,
                min_p = 0.05,
                frequency_penalty = 0,
                presence_penalty = 0,
                stream = false,
                think = false,
                messages
            };

            return JsonSerializer.Serialize(requestBody);
        }
    }
}