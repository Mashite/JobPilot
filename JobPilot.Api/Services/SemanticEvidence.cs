using System.Text.RegularExpressions;
using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

internal sealed record SemanticEvidence(MatchCitation Citation, string? Start = null, string? End = null)
{
    public static List<SemanticEvidence> Collect(CandidateProfile profile)
    {
        var evidence = new List<SemanticEvidence>();
        void Add(string? text, string kind, string? employer = null, string? project = null, string? role = null,
            IReadOnlyList<string>? technologies = null, string? start = null, string? end = null, string? persistedId = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            // JSON profile entries have no database row IDs. Reference the identity and content already stored in the snapshot.
            var id = persistedId ?? "cv-" + JobRequirementCache.Key(string.Join('\n', kind, employer, project, role, start, end, text));
            var excerpt = RemoveContactDetails(text.Trim(), profile).Trim();
            if (excerpt.Length > 0)
                evidence.Add(new(new MatchCitation(id, kind, excerpt[..Math.Min(excerpt.Length, 420)],
                    employer, project, role, technologies?.Take(10).ToArray()), start, end));
        }

        Add(profile.ProfessionalTitle, "professional title");
        foreach (var paragraph in (profile.Summary ?? "").Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries))
            Add(paragraph, "professional profile");
        foreach (var strength in profile.CoreStrengths ?? [])
            Add(strength, "core strength");
        foreach (var skill in profile.TechnicalSkills ?? [])
            Add(skill, "verified skill");
        foreach (var item in (profile.CareerEvidence ?? []).Where(item => item.Verification is
                     EvidenceVerification.ImportConfirmed or EvidenceVerification.ManuallyConfirmed))
        {
            Add(item.OriginalName + (item.ExperienceScope == "personal_project" ? " (personal project)" : "") +
                " · " + item.SourceExcerpt, "verified competency", item.Employer, item.Project,
                technologies: item.Category == "Used technologies" ? [item.OriginalName] : null,
                persistedId: item.Id == Guid.Empty ? null : item.Id.ToString("D"));
        }
        foreach (var role in profile.Experiences ?? [])
        {
            Add(role.Position, "employment", role.Company, role: role.Position, start: role.StartDate, end: role.EndDate);
            foreach (var text in role.Responsibilities ?? [])
                Add(text, "responsibility", role.Company, role: role.Position, technologies: role.Technologies, start: role.StartDate, end: role.EndDate);
            foreach (var text in role.Achievements ?? [])
                Add(text, "achievement", role.Company, role: role.Position, technologies: role.Technologies, start: role.StartDate, end: role.EndDate);
            foreach (var technology in role.Technologies ?? [])
                Add(technology, "used technology", role.Company, role: role.Position, technologies: [technology], start: role.StartDate, end: role.EndDate);
        }
        foreach (var project in profile.ProjectDetails ?? [])
        {
            Add(project.Name, "project", project.Employer, project.Name, technologies: project.Technologies);
            foreach (var paragraph in (project.Description ?? "").Split('\n'))
                Add(paragraph, "project", project.Employer, project.Name, technologies: project.Technologies);
            foreach (var achievement in project.Achievements ?? [])
                Add(achievement, "project achievement", project.Employer, project.Name, technologies: project.Technologies);
            foreach (var technology in project.Technologies ?? [])
                Add(technology, "used technology", project.Employer, project.Name, technologies: [technology]);
        }
        if (profile.ProjectDetails is not { Count: > 0 })
        {
            foreach (var project in profile.Projects ?? [])
                Add(project, "project");
        }
        foreach (var education in profile.EducationDetails ?? [])
            Add(string.Join(" · ", new[] { education.Degree, education.Institution, education.GraduationDate, education.Details }
                .Where(value => !string.IsNullOrWhiteSpace(value))), "education");
        if (profile.EducationDetails is not { Count: > 0 })
        {
            foreach (var education in profile.Education ?? [])
                Add(education, "education");
        }
        foreach (var certification in profile.Certifications ?? [])
            Add(string.Join(" · ", new[] { certification.Name, certification.Issuer, certification.Date }
                .Where(value => !string.IsNullOrWhiteSpace(value))), "certification");
        foreach (var theme in (profile.ImportReviewSections ?? []).Where(section => section.Kind == "themes" && !section.RequiresReview))
        {
            foreach (var line in theme.Lines)
                Add(line, "professional theme");
        }
        return evidence.DistinctBy(item => item.Citation.Id).ToList();
    }

    private static string RemoveContactDetails(string text, CandidateProfile profile)
    {
        foreach (var value in new[] { profile.FullName, profile.Email, profile.Phone, profile.LinkedIn, profile.GitHub })
        {
            if (!string.IsNullOrWhiteSpace(value))
                text = text.Replace(value, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }
        return Regex.Replace(text, @"\b[^\s@]+@[^\s@]+\.[^\s@]+", "[redacted]");
    }
}
