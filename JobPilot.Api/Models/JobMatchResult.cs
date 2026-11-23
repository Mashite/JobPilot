
namespace JobPilot.Api.Models;

public class JobMatchResult
{
    public string JobTitle { get; set; } = "";

    public int MatchScore { get; set; }

    public List<string> MatchingSkills { get; set; } = [];

    public List<string> MissingOrUnverifiedSkills { get; set; } = [];

    public List<string> Evidence { get; set; } = [];
    public List<JobRequirement> Requirements { get; set; } = [];
    public List<string> ExperienceRequirements { get; set; } = [];
    public List<string> Technologies { get; set; } = [];
    public List<string> Responsibilities { get; set; } = [];
    public List<RequirementMatch> RequirementMatches { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public int VerifiedSkillCount { get; set; }
    public int NormalizedVerifiedSkillCount { get; set; }
}

public sealed record RequirementMatch(
    JobRequirement Requirement, string Status, string? ProfileSkill, string Reason, bool CanConfirmSkill,
    bool CanSaveToMasterProfile = false, bool RequiresReviewEvidence = false, string? ReviewEvidence = null);
