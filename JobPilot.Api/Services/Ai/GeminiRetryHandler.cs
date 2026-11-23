using System.Text.Json;

namespace JobPilot.Api.Services.Ai;

internal sealed class GeminiRetryHandler(ILogger<GeminiRetryHandler> logger) : DelegatingHandler
{
    private const int MaxRetries = 2;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        for (var attempt = 0; !response.IsSuccessStatusCode; attempt++)
        {
            try
            {
                await LogFailureAsync(response, attempt + 1, cancellationToken);
                var status = (int)response.StatusCode;
                if (attempt >= MaxRetries || status is not (429 or 500 or 502 or 503 or 504))
                    return response;

                var retryAfter = response.Headers.RetryAfter;
                var wait = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromMilliseconds(250 * (1 << attempt));
                if (wait > MaxRetryAfter)
                    return response;

                response.Dispose();
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cancellationToken);
                using var retry = await CopyAsync(request, cancellationToken);
                response = await base.SendAsync(retry, cancellationToken);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        return response;
    }

    private async Task LogFailureAsync(HttpResponseMessage response, int attempt, CancellationToken cancellationToken)
    {
        string? status = null;
        string? reason = null;
        try
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (payload.RootElement.TryGetProperty("error", out var error))
            {
                status = error.TryGetProperty("status", out var value) ? value.GetString() : null;
                if (status is not ("INVALID_ARGUMENT" or "UNAUTHENTICATED" or "PERMISSION_DENIED" or "NOT_FOUND" or
                    "RESOURCE_EXHAUSTED" or "INTERNAL" or "UNAVAILABLE" or "DEADLINE_EXCEEDED" or "FAILED_PRECONDITION"))
                {
                    status = null;
                }

                if (error.TryGetProperty("details", out var details))
                {
                    reason = details.EnumerateArray()
                        .Where(detail => detail.TryGetProperty("@type", out var type) &&
                            type.GetString() == "type.googleapis.com/google.rpc.ErrorInfo")
                        .Select(detail => detail.TryGetProperty("reason", out var code) ? code.GetString() : null)
                        .FirstOrDefault(code => code is "API_KEY_INVALID" or "API_KEY_EXPIRED" or "API_KEY_SERVICE_BLOCKED" or
                            "API_KEY_HTTP_REFERRER_BLOCKED" or "API_KEY_IP_ADDRESS_BLOCKED" or "SERVICE_DISABLED" or
                            "BILLING_DISABLED" or "RATE_LIMIT_EXCEEDED" or "QUOTA_EXCEEDED" or "CONSUMER_INVALID");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or HttpRequestException)
        {
            // Some gateways return HTML or an empty body. The HTTP status is still diagnostic.
        }

        // Google's free-text messages and metadata may echo prompts, credentials or account details.
        logger.LogWarning("Gemini HTTP {UpstreamStatusCode} on attempt {Attempt}. Google status: {GoogleStatus}. Google reason: {GoogleReason}.",
            (int)response.StatusCode, attempt, status, reason);
    }

    private static async Task<HttpRequestMessage> CopyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var copy = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = content is null ? null : new ByteArrayContent(content)
        };
        foreach (var header in request.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null && copy.Content is not null)
        {
            foreach (var header in request.Content.Headers)
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return copy;
    }
}
