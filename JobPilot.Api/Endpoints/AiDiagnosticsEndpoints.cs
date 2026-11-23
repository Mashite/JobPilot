using System.Diagnostics;
using System.Net;
using JobPilot.Api.Services.Ai;

namespace JobPilot.Api.Endpoints;

internal static class AiDiagnosticsEndpoints
{
    public static void MapAiDiagnostics(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/ai/test-connection", TestConnection)
            .AddEndpointFilter((context, next) =>
            {
                var connection = context.HttpContext.Connection;
                var remote = connection.RemoteIpAddress;
                if (remote?.IsIPv4MappedToIPv6 == true)
                    remote = remote.MapToIPv4();

                var headers = context.HttpContext.Request.Headers;
                if (remote is null || !IPAddress.IsLoopback(remote) ||
                    headers.ContainsKey("Origin") || headers.ContainsKey("Sec-Fetch-Site"))
                {
                    return ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
                }

                return next(context);
            });

    private static async Task<IResult> TestConnection(
        IAiModelProvider provider, AiProviderSelection selected, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var reply = await provider.GenerateAsync(new AiRequest("", [new AiUserMessage("Say Hello JobPilot.")]), deadline.Token);
            if (string.IsNullOrWhiteSpace(reply.Text))
                throw new AiProviderException("The selected provider returned no text.", diagnosticCode: "empty_response");

            var response = reply.Text.Trim();
            return Results.Ok(new
            {
                provider = selected.Name, model = selected.Model, success = true,
                elapsedMs = ElapsedMilliseconds(started), response = response[..Math.Min(response.Length, 200)]
            });
        }
        catch (AiProviderException ex)
        {
            return Failure(selected, started, ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(selected, started,
                new AiProviderException("The connection test timed out.", 504, diagnosticCode: "timeout"));
        }
    }

    private static IResult Failure(AiProviderSelection selection, long started, AiProviderException error) => Results.Json(new
    {
        provider = selection.Name, model = selection.Model, success = false, elapsedMs = ElapsedMilliseconds(started),
        diagnosticCode = error.DiagnosticCode ?? "provider_error", providerStatusCode = error.ProviderStatusCode, error = error.Message
    }, statusCode: error.StatusCode);

    private static long ElapsedMilliseconds(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
