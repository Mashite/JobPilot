
namespace JobPilot.Api.Models;

public class JobRequirement
{
    public string Skill { get; set; } = "";

    // "required" or "preferred"
    public string Importance { get; set; } = "required";
    public string Category { get; set; } = "technology";
    public int? MinimumExperienceMonths { get; set; }
    public string OriginalText { get; set; } = "";
}
