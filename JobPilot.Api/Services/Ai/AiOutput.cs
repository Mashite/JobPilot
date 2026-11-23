using System.Text.Json;

namespace JobPilot.Api.Services.Ai;

internal static class AiOutput
{
    public static T ReadJson<T>(string text, string operation) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonSerializerOptions.Web)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new AiProviderException($"The AI provider returned invalid JSON for {operation}.");
        }
    }
}
