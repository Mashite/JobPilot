
namespace JobPilot.Api.Models;

public class CandidateProfile
{
    public string FullName { get; set; } = "";
    public string ProfessionalTitle { get; set; } = "";
    public string Summary { get; set; } = "";

    public List<string> TechnicalSkills { get; set; } = [];
    public List<WorkExperience> Experiences { get; set; } = [];
    public List<string> Projects { get; set; } = [];
    public List<string> Education { get; set; } = [];
}
