
using JobPilot.Api.Models;
using System.Net.Mail;

namespace JobPilot.Api.Services;

public class CandidateProfileValidator
{
    public Dictionary<string, string[]> ValidateForSave(CandidateProfile profile)
    {
        var errors = new Dictionary<string, string[]>();
        void Check(bool valid, string field, string message)
        {
            if (!valid)
                errors[field] = [message];
        }

        Check(Required(profile.FullName), "FullName", "Enter a full name of at most 250 characters.");
        Check(Required(profile.ProfessionalTitle), "ProfessionalTitle", "Enter a professional title of at most 250 characters.");
        Check(Fits(profile.Summary, 8_000), "Summary", "Keep the summary to 8,000 characters.");
        Check(string.IsNullOrWhiteSpace(profile.Email) ||
            (Fits(profile.Email, 254) && MailAddress.TryCreate(profile.Email, out var address) && address.Address == profile.Email),
            "Email", "Enter an email address.");
        Check(Fits(profile.Phone, 60), "Phone", "Keep the phone number to 60 characters.");
        Check(Fits(profile.Location), "Location", "Keep the location to 250 characters.");
        Check(IsWebUrl(profile.LinkedIn), "LinkedIn", "Enter a valid HTTP or HTTPS link.");
        Check(IsWebUrl(profile.GitHub), "GitHub", "Enter a valid HTTP or HTTPS link.");
        Check(profile.TechnicalSkills is { Count: <= 500 } && profile.TechnicalSkills.All(CandidateProfileService.IsValidSkill),
            "TechnicalSkills", "Enter up to 500 skills with valid names of at most 120 characters.");
        Check(profile.Experiences is { Count: <= 100 } && profile.Experiences.All(experience =>
                experience is not null && Required(experience.Company) && Required(experience.Position) &&
                Required(experience.StartDate, 60) &&
                (experience.IsCurrentPosition || Required(experience.EndDate, 60)) &&
                ValidLines(experience.Responsibilities) && ValidLines(experience.Achievements) &&
                ValidLines(experience.Technologies, 120)),
            "Experiences", "Each experience needs a company, title, start date, and an end date or current position.");
        Check(profile.EducationDetails is null
                ? ValidLines(profile.Education)
                : profile.EducationDetails is { Count: <= 100 } && profile.EducationDetails.All(education =>
                    education is not null && Required(education.Degree, 1_000) && Fits(education.Institution) &&
                    Fits(education.GraduationDate, 60) && Fits(education.Details, 4_000)),
            "Education", "Each education entry needs a degree or credential. Check field lengths.");
        Check(profile.Certifications is { Count: <= 100 } && profile.Certifications.All(certification =>
                certification is not null && Required(certification.Name) && Fits(certification.Issuer) &&
                Fits(certification.Date, 60) && IsWebUrl(certification.Url)),
            "Certifications", "Each certification needs a name. Optional links must use HTTP or HTTPS.");
        Check(profile.ProjectDetails is null
                ? ValidLines(profile.Projects)
                : profile.ProjectDetails is { Count: <= 100 } && profile.ProjectDetails.All(project =>
                    project is not null && Required(project.Name, 1_000) && Fits(project.Description, 4_000) &&
                    IsWebUrl(project.Url) && ValidLines(project.Technologies, 120) && ValidLines(project.Achievements)),
            "Projects", "Each project needs a name. Check field lengths and optional HTTP or HTTPS links.");
        return errors;
    }

    private static bool Required(string? text, int maxLength = 250) =>
        !string.IsNullOrWhiteSpace(text) && Fits(text, maxLength);

    private static bool Fits(string? text, int maxLength = 250) => text is null || text.Length <= maxLength;

    private static bool ValidLines(List<string>? values, int maxLength = 4_000) =>
        values is { Count: <= 200 } && values.All(value => Required(value, maxLength));

    private static bool IsWebUrl(string? text) =>
        string.IsNullOrWhiteSpace(text) || (Fits(text, 2_000) &&
            Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host));

    public List<string> Validate(CandidateProfile profile)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.FullName))
            errors.Add("Full name is required.");

        if (string.IsNullOrWhiteSpace(profile.ProfessionalTitle))
            errors.Add("Professional title is required.");

        if (profile.TechnicalSkills is null ||
            profile.TechnicalSkills.Count == 0)
        {
            errors.Add("At least one technical skill is required.");
        }

        if (profile.Experiences is null ||
            profile.Experiences.Count == 0)
        {
            errors.Add("At least one work experience is required.");
        }
        else
        {
            foreach (var experience in profile.Experiences)
            {
                if (string.IsNullOrWhiteSpace(experience.Company))
                    errors.Add("Experience company is required.");

                if (string.IsNullOrWhiteSpace(experience.Position))
                    errors.Add("Experience position is required.");

                if (string.IsNullOrWhiteSpace(experience.StartDate))
                    errors.Add(
                        $"Start date is missing for {experience.Company}.");

                if (string.IsNullOrWhiteSpace(experience.EndDate))
                    errors.Add(
                        $"End date is missing for {experience.Company}. " +
                        "Use 'Present' for current employment.");
            }
        }

        return errors;
    }
}
