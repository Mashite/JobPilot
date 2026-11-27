namespace JobPilot.Api;

public record AnalyzeJobRequest(string JobDescription);

public record GenerateResumeRequest(
    string JobDescription, List<string>? ConfirmedSkills = null, List<string>? ConfirmedEvidence = null,
    bool IncludeAllExperiences = false);
