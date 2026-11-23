
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobPilot.Api.Models;

public class CandidateProfile : ProfileEntry
{
    public string FullName { get; set; } = "";
    public string ProfessionalTitle { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Location { get; set; }
    public string? LinkedIn { get; set; }
    public string? GitHub { get; set; }

    public List<string> TechnicalSkills { get; set; } = [];
    public List<WorkExperience> Experiences { get; set; } = [];
    public List<string> Projects { get; set; } = [];
    public List<string> Education { get; set; } = [];
    public List<ProfileEducation>? EducationDetails { get; set; }
    public List<ProfileCertification> Certifications { get; set; } = [];
    public List<ProfileProject>? ProjectDetails { get; set; }
}

public abstract class ProfileEntry
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}

public class ProfileEducation : ProfileEntry
{
    public string Degree { get; set; } = "";
    public string? Institution { get; set; }
    public string? GraduationDate { get; set; }
    public string? Details { get; set; }
}

public class ProfileCertification : ProfileEntry
{
    public string Name { get; set; } = "";
    public string? Issuer { get; set; }
    public string? Date { get; set; }
    public string? Url { get; set; }
}

public class ProfileProject : ProfileEntry
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Technologies { get; set; } = [];
    public List<string> Achievements { get; set; } = [];
    public string? Url { get; set; }
}
