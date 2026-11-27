namespace JobPilot.Api.Data.Entities;

public sealed class CandidateIntelligenceRecord
{
    public string EvaluationKey { get; set; } = "";
    public string ProfileVersion { get; set; } = "";
    public string Payload { get; set; } = "";
}

public sealed class ExperienceDiscoveryRecord
{
    public string SkillKey { get; set; } = "";
    public string ResponseJson { get; set; } = "";
}
