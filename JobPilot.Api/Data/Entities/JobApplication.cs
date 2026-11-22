
namespace JobPilot.Api.Data.Entities;

public class JobApplication
{
    public int Id { get; set; }

    public string JobTitle { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string JobDescription { get; set; } = "";

    public int? MatchScore { get; set; }

    public string Status { get; set; } = "Interested";

    public DateTime CreatedAtUtc { get; set; } =
        DateTime.UtcNow;

    public List<SavedResume> Resumes { get; set; } = [];
}
