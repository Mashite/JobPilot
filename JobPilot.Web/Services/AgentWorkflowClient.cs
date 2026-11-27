namespace JobPilot.Web.Services;

internal static class AgentWorkflowClient
{
    public static HttpRequestMessage Request(HttpMethod method, Guid id, string suffix = "")
    {
        var request = new HttpRequestMessage(method, $"api/agent/workflows/{id:D}{suffix}");
        request.Headers.Add("X-JobPilot-Profile-Write", "1");
        return request;
    }
}
