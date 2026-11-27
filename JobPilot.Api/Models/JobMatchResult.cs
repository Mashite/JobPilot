
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
    public string? SemanticStatus { get; set; }
    public string? SemanticFailure { get; set; }
    public string? SemanticResumeKey { get; set; }
    public List<SemanticBatchFailure> SemanticBatchFailures { get; set; } = [];
    public bool IsScoreProvisional { get; set; }
    public JobReview? SavedReview { get; set; }
    public JobMatchResult? AutomaticResult { get; set; }
    public JobSuitabilityAssessment? Suitability { get; set; }
}

public sealed record RequirementMatch(
    JobRequirement Requirement, string Status, string? ProfileSkill, string Reason, bool CanConfirmSkill,
    bool CanSaveToMasterProfile = false, bool RequiresReviewEvidence = false, string? ReviewEvidence = null,
    string? EvidenceExcerpt = null, string? EvidenceEmployer = null, string? EvidenceProject = null,
    IReadOnlyList<MatchCitation>? Citations = null, bool RetainsPartialCredit = false);

public sealed record SemanticBatchFailure(int Batch, int Total, string Code, int PendingRequirements);

public sealed record JobSuitabilityAssessment(int AreaCoverage, IReadOnlyList<CompetencyAreaCoverage> Areas,
    IReadOnlyList<string> CriticalGaps, IReadOnlyList<string> ImportantGaps, IReadOnlyList<string> NeedingClarification,
    IReadOnlyList<string> Transferable);
public sealed record CompetencyAreaCoverage(string Name, int Coverage, int RequirementCount);

public sealed record MatchCitation(string Id, string Kind, string Excerpt, string? Employer = null,
    string? Project = null, string? Role = null, IReadOnlyList<string>? Technologies = null);

public sealed record JobReview(string[] Confirmed, string[] Related, string[] Missing, Dictionary<string, string> Evidence);
