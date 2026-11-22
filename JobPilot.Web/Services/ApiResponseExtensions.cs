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

        var message = response.StatusCode == HttpStatusCode.BadRequest
            ? await ReadValidationMessageAsync(response.Content, cancellationToken)
            : null;

        throw new HttpRequestException(
            message ?? $"The JobPilot API returned HTTP {(int)response.StatusCode}. Try again.",
            null, response.StatusCode);
    }

    private static async Task<string?> ReadValidationMessageAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.String)
                return root.GetString();

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) &&
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
