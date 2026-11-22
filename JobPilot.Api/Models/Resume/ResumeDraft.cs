
namespace JobPilot.Api.Models.Resume;

public class ResumeDraft
{
    public string ProfessionalTitle { get; set; } = "";
    public string ProfessionalSummary { get; set; } = "";

    // Must reference skills already present in CandidateProfile.
    public List<string> SelectedSkills { get; set; } = [];

    // Indices into CandidateProfile.Experiences.
    public List<int> SelectedExperienceIndices { get; set; } = [];
}
