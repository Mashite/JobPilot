using System.Text.Json;

namespace JobPilot.Api.Services.Ai;

public interface IAiModelProvider
{
    Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default);
}

public sealed record AiRequest(
    string Instructions,
    IReadOnlyList<AiMessage> History,
    JsonElement? OutputSchema = null,
    IReadOnlyList<AiTool>? Tools = null,
    double? Temperature = null,
    int? MaxOutputTokens = null);

public abstract record AiMessage;
public sealed record AiUserMessage(string Text) : AiMessage;
public sealed record AiAssistantMessage(string Text, IReadOnlyList<AiToolCall> ToolCalls) : AiMessage
{
    // The originating adapter can replay the exact turn without exposing it to the agent.
    internal JsonElement? ProviderContent { get; init; }
}
public sealed record AiTool(string Name, string Description, JsonElement Parameters);
public sealed record AiToolCall(string Name, JsonElement Arguments, string? Id = null);
public sealed record AiToolResultMessage(AiToolCall Call, JsonElement Result) : AiMessage;
