using System.Net;
using System.Text.Json;

namespace JobPilot.Api.Services.Ai;

internal static class AiProviderHttp
{
    public static async Task<AiAssistantMessage> GenerateAsync(
        HttpClient client, HttpRequestMessage request, string provider, string model,
        Func<JsonElement, AiAssistantMessage> mapResponse, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw ToError(response.StatusCode, provider, model);

            using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
            return mapResponse(json?.RootElement ?? default);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException($"{provider} timed out. Try again.", StatusCodes.Status504GatewayTimeout);
        }
        catch (HttpRequestException)
        {
            throw new AiProviderException($"Could not reach {provider}. Check the provider connection and try again.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            throw new AiProviderException($"{provider} returned a malformed response.");
        }
    }

    private static AiProviderException ToError(HttpStatusCode status, string provider, string model) => status switch
    {
        HttpStatusCode.NotFound => new($"The configured {provider} model '{model}' is unavailable. Check the model setting and access."),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new($"{provider} denied access. Check the provider credentials and model access."),
        HttpStatusCode.TooManyRequests => new($"{provider} rate limit or quota reached. Check your quota and try again later.", 429),
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new($"{provider} timed out. Try again.", 504),
        HttpStatusCode.ServiceUnavailable => new($"{provider} is temporarily unavailable. Try again later.", 503),
        HttpStatusCode.BadRequest => new($"{provider} rejected the request. Check that the configured model supports this AI feature."),
        _ => new($"{provider} returned HTTP {(int)status}. Try again.")
    };
}
