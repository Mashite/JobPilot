using System.Net;
using System.Text.Json;

namespace JobPilot.Web.Services;

internal static class ApiResponseExtensions
{
    public static async Task EnsureJobPilotSuccessAsync(
        this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
            return;

        var validation = response.StatusCode == HttpStatusCode.BadRequest;
        var message = validation || response.Content.Headers.ContentType?.MediaType == "application/problem+json"
            ? await ReadErrorMessageAsync(response.Content, validation, cancellationToken)
            : null;

        throw new HttpRequestException(
            message ?? $"The JobPilot API returned HTTP {(int)response.StatusCode}. Try again.",
            null, response.StatusCode);
    }

    private static async Task<string?> ReadErrorMessageAsync(HttpContent content, bool validation, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (validation && root.ValueKind == JsonValueKind.String)
                return root.GetString();

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String &&
                title.GetString() is ("AI request failed" or "Master CV could not be loaded" or
                    "Master CV storage is unavailable." or "Job review storage is unavailable." or "Agent workflow storage is unavailable." or
                    "Candidate intelligence storage is unavailable."))
            {
                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(detail.GetString()))
                    return detail.GetString();
                return title.GetString();
            }

            if (validation && root.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject()
                    .Where(field => field.Value.ValueKind == JsonValueKind.Array)
                    .SelectMany(field => field.Value.EnumerateArray())
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString())
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .Distinct();
                var summary = string.Join(" ", messages);
                return summary.Length > 0 ? summary : null;
            }
        }
        catch (JsonException)
        {
            // Fall back to the status code for an empty or unexpected response body.
        }

        return null;
    }
}
