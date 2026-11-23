
namespace JobPilot.Api.Models;

public class JobAnalysis
{
    public string JobTitle { get; set; } = "";

    public List<JobRequirement> Requirements { get; set; } = [];
    public List<string> ExperienceRequirements { get; set; } = [];
    public List<string> Technologies { get; set; } = [];
    public List<string> Responsibilities { get; set; } = [];
    internal int? ExtractedItemCount { get; init; }
}
