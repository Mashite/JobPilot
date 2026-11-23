
namespace JobPilot.Api.Models.Resume;

public class ResumeDraft
{
    // Must reference skills already present in CandidateProfile.
    public List<string> SelectedSkills { get; set; } = [];

    // Indices into CandidateProfile.Experiences.
    public List<int> SelectedExperienceIndices { get; set; } = [];
}
