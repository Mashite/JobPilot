using System.Text.Json;
using JobPilot.Web.Models;

namespace JobPilot.Web.Services;

internal sealed class AgentResumeDraftState
{
    public AgentResumeDraft? Current { get; private set; }

    public void Open(GeneratedResume resume, string? jobDescription)
    {
        // The editor mutates its model. Keep the original chat result intact.
        var copy = JsonSerializer.SerializeToElement(resume, JsonSerializerOptions.Web)
            .Deserialize<GeneratedResume>(JsonSerializerOptions.Web)!;
        Current = new AgentResumeDraft(copy, jobDescription ?? "");
    }
}

internal sealed record AgentResumeDraft(GeneratedResume Resume, string JobDescription)
{
    public int? ApplicationId { get; set; }
    public int? CreatedApplicationId { get; set; }
}
