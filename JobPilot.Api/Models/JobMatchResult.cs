
namespace JobPilot.Api.Models;

public class JobMatchResult
{
    public string JobTitle { get; set; } = "";

    public int MatchScore { get; set; }

    public List<string> MatchingSkills { get; set; } = [];

    public List<string> MissingOrUnverifiedSkills { get; set; } = [];

    public List<string> Evidence { get; set; } = [];
    public List<JobRequirement> Requirements { get; set; } = [];
}
