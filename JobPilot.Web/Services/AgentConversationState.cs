using JobPilot.Web.Models.Agent;

namespace JobPilot.Web.Services;

internal sealed class AgentConversationState
{
    public List<ChatMessage> Messages { get; } = [];
    public (Guid Id, AgentChatRequest Request)? LastWorkflow { get; set; }
    public bool CanRetry { get; set; }
    public string? Error { get; set; }
}
