namespace JobPilot.Api.Data.Entities;

public sealed class AgentWorkflowRecord
{
    public Guid Id { get; set; }
    public string InputHash { get; set; } = "";
    public string JobDescription { get; set; } = "";
    public string? Intent { get; set; }
    public string Stage { get; set; } = "intent";
    public string State { get; set; } = "running";
    public string? Failure { get; set; }
    public string? RequirementsJson { get; set; }
    public string? AnalysisJson { get; set; }
    public string? ResumeJson { get; set; }
    public string? Reply { get; set; }
}
