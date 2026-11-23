
namespace JobPilot.Api.Models;

public class WorkExperience : ProfileEntry
{
    public string Company { get; set; } = "";
    public string Position { get; set; } = "";
    public string StartDate { get; set; } = "";
    public string? EndDate { get; set; }
    public bool IsCurrentPosition { get; set; }
    public List<string> Responsibilities { get; set; } = [];
    public List<string> Achievements { get; set; } = [];
    public List<string> Technologies { get; set; } = [];
}
