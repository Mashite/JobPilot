using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobPilot.Web.Models.Agent;

public sealed record AgentChatRequest(string Message, string? JobDescription = null);

public sealed record AgentChatResponse(
    [property: JsonRequired] string Message,
    [property: JsonRequired] List<AgentToolResult> ExecutedTools);

public sealed record AgentToolResult(
    [property: JsonRequired] string ToolName,
    [property: JsonRequired] JsonElement Result);

public sealed record ChatMessage(string Role, string Content, List<AgentToolResult> Tools);
