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
        return preparedRaw.Contains("<")
            ? config.ExecutionValues.StructuredTextModel
            : config.ExecutionValues.StandardModel;
    }

    public static string GenerateLlmRequestData(ModelExecutionConfig modelConfig, List<object> messages)
    {
        if (modelConfig.ModelParams != null)
        {
            // Create a dynamic object and populate it with Params
            dynamic requestBody = new ExpandoObject();
            requestBody.model = modelConfig.Model;
            requestBody.stream = false;
            requestBody.messages = messages;

            // Add each key-value pair from Params to the dynamic object
            var requestBodyDict = (IDictionary<string, object>)requestBody;
            foreach (var param in modelConfig.ModelParams)
                if (decimal.TryParse(param.Value.ToString(), out var param2))
                    requestBodyDict[param.Key] = param2;
                else
                    requestBodyDict[param.Key] = param.Value;

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
                messages
            };

            return JsonSerializer.Serialize(requestBody);
        }
    }
}