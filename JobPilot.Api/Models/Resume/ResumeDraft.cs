
using System.Text.Json.Serialization;

namespace JobPilot.Api.Models.Resume;

public class ResumeDraft
{
    // Must reference skills already present in CandidateProfile.
    [JsonRequired]
    public List<string> SelectedSkills { get; set; } = [];

    // Indices into CandidateProfile.Experiences.
    [JsonRequired]
    public List<int> SelectedExperienceIndices { get; set; } = [];
}
