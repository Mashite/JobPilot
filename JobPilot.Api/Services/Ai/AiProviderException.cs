namespace JobPilot.Api.Services.Ai;

public sealed class AiProviderException(
    string message,
    int statusCode = StatusCodes.Status502BadGateway,
    int? providerStatusCode = null,
    string? diagnosticCode = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public int? ProviderStatusCode { get; } = providerStatusCode;
    public string? DiagnosticCode { get; } = diagnosticCode;
}
