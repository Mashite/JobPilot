namespace JobPilot.Api.Models.Agent;

public sealed record AgentRequest(
    string Message,
    string? JobDescription = null);