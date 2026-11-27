using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobPilot.Web.Models;

internal sealed class CandidateProfileForm : ProfileFormEntry
{
    public string? FullName { get; set; }
    public string? ProfessionalTitle { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Location { get; set; }
    public string? LinkedIn { get; set; }
    public string? GitHub { get; set; }
    public string? Summary { get; set; }
    public List<string> TechnicalSkills { get; set; } = [];
    public List<WorkExperienceForm> Experiences { get; set; } = [];
    public List<string> Education { get; set; } = [];
    public List<string> Projects { get; set; } = [];
    public List<EducationForm>? EducationDetails { get; set; }
    public List<CertificationForm> Certifications { get; set; } = [];
    public List<ProjectForm>? ProjectDetails { get; set; }
    public List<string> Languages { get; set; } = [];
    public List<string> CoreStrengths { get; set; } = [];
    public List<CareerEvidenceForm> CareerEvidence { get; set; } = [];
    public List<CareerSourceSectionForm> ImportReviewSections { get; set; } = [];

    public void PrepareForEditing()
    {
        EducationDetails ??= Education.Select(degree => new EducationForm { Degree = degree }).ToList();
        ProjectDetails ??= Projects.Select(name => new ProjectForm { Name = name }).ToList();
        foreach (var experience in Experiences)
        {
            if (string.Equals(experience.EndDate?.Trim(), "Present", StringComparison.OrdinalIgnoreCase))
                experience.IsCurrentPosition = true;
        }
    }
}

internal sealed class CareerEvidenceForm
{
    public Guid Id { get; set; }
    public string OriginalName { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Classification { get; set; } = "";
    public string Verification { get; set; } = "Unverified";
    public string? Employer { get; set; }
    public string? Project { get; set; }
    public string SourceExcerpt { get; set; } = "";
    public string SourceSection { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public int? ConfirmedExperienceMonths { get; set; }
    [JsonIgnore] public bool Include { get; set; } = true;
}

internal sealed record CareerSourceSectionForm(string Heading, List<string> Lines, bool RequiresReview, string? Kind = null);

internal abstract class ProfileFormEntry
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}

internal sealed class WorkExperienceForm : ProfileFormEntry
{
    public string? Company { get; set; }
    public string? Location { get; set; }
    public string? Position { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public bool IsCurrentPosition { get; set; }
    public List<string> Responsibilities { get; set; } = [];
    public List<string> Achievements { get; set; } = [];
    public List<string> Technologies { get; set; } = [];
}

internal sealed class EducationForm : ProfileFormEntry
{
    public string? Degree { get; set; }
    public string? Institution { get; set; }
    public string? GraduationDate { get; set; }
    public string? Details { get; set; }
}

internal sealed class CertificationForm : ProfileFormEntry
{
    public string? Name { get; set; }
    public string? Issuer { get; set; }
    public string? Date { get; set; }
    public string? Url { get; set; }
}

internal sealed class ProjectForm : ProfileFormEntry
{
    public string? Name { get; set; }
    public string? Employer { get; set; }
    public string? Description { get; set; }
    public List<string> Technologies { get; set; } = [];
    public List<string> Achievements { get; set; } = [];
    public string? Url { get; set; }
}
