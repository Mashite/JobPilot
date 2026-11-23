namespace JobPilot.Api.Services.Ai;

public sealed class AiProviderException(string message, int statusCode = StatusCodes.Status502BadGateway) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
