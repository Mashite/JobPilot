
namespace JobPilot.Api.Data.Entities;

public class SavedResume
{
    public int Id { get; set; }

    public int JobApplicationId { get; set; }

    public JobApplication JobApplication { get; set; } = null!;

    public string ResumeJson { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } =
        DateTime.UtcNow;
}
