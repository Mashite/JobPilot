using System.Text.Json;

namespace JobPilot.Api.Models.Agent;

public sealed record AgentToolExecution(
    string ToolName,
    JsonElement Result);

public sealed record AgentResponse(
    string Message,
    List<AgentToolExecution> ExecutedTools);