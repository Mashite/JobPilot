
namespace JobPilot.Api.Models.Resume;

public class GeneratedResume
{
    public string FullName { get; set; } = "";
    public string ProfessionalTitle { get; set; } = "";
    public string ProfessionalSummary { get; set; } = "";

    public List<string> TechnicalSkills { get; set; } = [];

    public List<ResumeExperience> Experiences { get; set; } = [];

    public List<string> Projects { get; set; } = [];

    public List<string> Education { get; set; } = [];
}

public class ResumeExperience
{
    public string Company { get; set; } = "";
    public string Position { get; set; } = "";
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";

    public List<string> Highlights { get; set; } = [];
}
