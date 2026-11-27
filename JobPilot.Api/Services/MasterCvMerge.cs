using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

internal sealed class MasterCvMerge(SkillNormalizer normalizer)
{
    public static List<string> FindConflicts(CandidateProfile current, CandidateProfile incoming)
    {
        var conflicts = new List<string>();
        if (Conflicts(current.FullName, incoming.FullName))
            conflicts.Add("A different name is already saved. The existing name will be kept.");
        if (Conflicts(current.ProfessionalTitle, incoming.ProfessionalTitle))
            conflicts.Add("A different professional title is already saved. The existing title will be kept.");
        if (Conflicts(current.Summary, incoming.Summary))
            conflicts.Add("The saved professional profile differs from this document. It will be kept unless you select the option to replace it.");
        if (Conflicts(current.Location, incoming.Location))
            conflicts.Add("A different location is already saved. The existing location will be kept.");
        foreach (var record in incoming.Experiences.Where(record => current.Experiences.Any(existing =>
            SameCompany(existing, record) && !SameRole(existing, record))))
        {
            conflicts.Add($"An existing position for {record.Company} differs. Review it in Master CV; the import will skip the conflicting position.");
        }
        return conflicts.Distinct(StringComparer.Ordinal).ToList();
    }

    public CandidateProfile Merge(CandidateProfile current, CandidateProfile incoming, bool replaceProfessionalProfile = false)
    {
        current.FullName = PreferExisting(current.FullName, incoming.FullName);
        current.ProfessionalTitle = PreferExisting(current.ProfessionalTitle, incoming.ProfessionalTitle);
        current.Summary = replaceProfessionalProfile && !string.IsNullOrWhiteSpace(incoming.Summary)
            ? incoming.Summary : PreferExisting(current.Summary, incoming.Summary);
        current.Location = PreferExisting(current.Location, incoming.Location);
        current.Languages = Union(current.Languages, incoming.Languages);
        current.CoreStrengths = Union(current.CoreStrengths, incoming.CoreStrengths);

        foreach (var record in incoming.Experiences)
        {
            var existing = current.Experiences.FirstOrDefault(candidate => SameRole(candidate, record));
            if (existing is not null)
            {
                existing.Location = PreferExisting(existing.Location, record.Location);
                if (!string.IsNullOrWhiteSpace(record.Location) && Same(CompanyKey(existing.Company), record.Company) &&
                    !Same(existing.Company, record.Company))
                    existing.Company = record.Company; // Upgrade the previous combined employer/location field.
                existing.Responsibilities = Union(existing.Responsibilities, record.Responsibilities);
                existing.Achievements = Union(existing.Achievements, record.Achievements);
                existing.Technologies = Union(existing.Technologies, record.Technologies)
                    .DistinctBy(normalizer.Normalize, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else if (!current.Experiences.Any(candidate => SameCompany(candidate, record)))
                current.Experiences.Add(record);
        }

        current.Experiences = current.Experiences.OrderByDescending(record => Year(record.StartDate)).ToList();
        current.EducationDetails ??= current.Education.Select(degree => new ProfileEducation { Degree = degree }).ToList();
        current.ProjectDetails ??= current.Projects.Select(name => new ProfileProject { Name = name }).ToList();
        foreach (var entry in incoming.EducationDetails ?? [])
        {
            if (!current.EducationDetails.Any(existing => Same(existing.Degree, entry.Degree)))
                current.EducationDetails.Add(entry);
        }
        foreach (var entry in incoming.ProjectDetails ?? [])
        {
            var existing = current.ProjectDetails.FirstOrDefault(project => MasterCvImporter.AreSameProject(project.Name, entry.Name) &&
                (string.IsNullOrWhiteSpace(project.Employer) || Same(project.Employer, entry.Employer)));
            if (existing is null)
                current.ProjectDetails.Add(entry);
            else
            {
                existing.Employer = PreferExisting(existing.Employer, entry.Employer);
                existing.Description = PreferExisting(existing.Description, entry.Description);
                existing.Technologies = Union(existing.Technologies, entry.Technologies);
                existing.Achievements = Union(existing.Achievements, entry.Achievements);
            }
        }
        foreach (var entry in incoming.Certifications)
        {
            if (!current.Certifications.Any(existing => Same(existing.Name, entry.Name)))
                current.Certifications.Add(entry);
        }
        foreach (var section in incoming.ImportReviewSections.Where(section => section.RequiresReview ||
            section.Kind is "employment" or "themes" or "philosophy"))
        {
            var existing = current.ImportReviewSections.FindIndex(existing => Same(existing.Heading, section.Heading) &&
                existing.Lines.SequenceEqual(section.Lines));
            if (existing < 0)
            {
                current.ImportReviewSections.Add(section);
            }
            else if (current.ImportReviewSections[existing].Kind is null && section.Kind is not null)
                current.ImportReviewSections[existing] = section;
        }

        var reviewed = incoming.CareerEvidence.Where(item => CandidateProfileService.IsValidSkill(item.OriginalName) &&
                (item.Category != "Used technologies" || incoming.Experiences.Any(role => Same(role.Company, item.Employer) &&
                    role.Technologies.Any(technology => normalizer.Normalize(technology) == normalizer.Normalize(item.OriginalName)))))
            .Select(item => item with
            {
                Id = Guid.NewGuid(),
                NormalizedName = normalizer.Normalize(item.OriginalName),
                UpdatedAt = DateTimeOffset.UtcNow,
                ConfirmedExperienceMonths = null,
                Verification = item.Verification switch
                {
                    EvidenceVerification.ManuallyConfirmed => EvidenceVerification.ManuallyConfirmed,
                    EvidenceVerification.ImportConfirmed when item.Classification == EvidenceClassification.ExplicitlyDocumented &&
                        item.SourceExcerpt.Contains(item.OriginalName, StringComparison.OrdinalIgnoreCase)
                            => EvidenceVerification.ImportConfirmed,
                    _ => EvidenceVerification.Unverified
                }
            }).ToList();
        CandidateProfileService.AddEvidence(current, reviewed);
        return current;
    }

    private static bool SameCompany(WorkExperience a, WorkExperience b) => Same(CompanyKey(a.Company), CompanyKey(b.Company));

    private static string CompanyKey(string company) =>
        company.Split([" | ", " — ", " – ", " - ", " ("], StringSplitOptions.None)[0];

    private static bool SameRole(WorkExperience a, WorkExperience b) =>
        SameCompany(a, b) && Same(a.Position, b.Position) && Same(a.StartDate, b.StartDate) && Same(a.EndDate, b.EndDate);

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Conflicts(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && !Same(a, b);

    private static string PreferExisting(string? existing, string? incoming) =>
        !string.IsNullOrWhiteSpace(existing) ? existing : incoming?.Trim() ?? "";

    private static List<string> Union(IEnumerable<string> existing, IEnumerable<string> added) =>
        existing.Concat(added).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static int Year(string? date)
    {
        var match = System.Text.RegularExpressions.Regex.Match(date ?? "", @"\b(?:19|20)\d{2}\b");
        return int.TryParse(match.Value, out var year) ? year : 0;
    }
}
