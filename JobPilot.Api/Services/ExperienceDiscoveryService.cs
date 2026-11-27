using System.Text.Json;
using JobPilot.Api.Data;
using JobPilot.Api.Data.Entities;
using JobPilot.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace JobPilot.Api.Services;

internal sealed class ExperienceDiscoveryService(JobPilotDbContext database, CandidateProfileService profiles, SkillNormalizer normalizer)
{
    private const string DiscoverySection = "Experience discovery";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly (string Group, string[] Skills)[] Catalog =
    [
        ("Database engineering", ["Stored procedures", "SQL functions", "Triggers", "Views", "CTEs", "Dynamic SQL", "Indexing",
            "Execution plan analysis", "Query optimization", "Database transactions", "Deadlock troubleshooting"]),
        ("Software architecture", ["SOLID principles", "Dependency injection", "Repository pattern", "Factory pattern", "Strategy pattern",
            "Layered architecture", "Clean Architecture", "Microservices"]),
        ("Security", ["ASP.NET Core Identity", "JWT authentication", "Role-based authorization", "Claims-based authorization",
            "Secure configuration management", "OWASP security practices"]),
        ("DevOps and cloud", ["Azure DevOps", "YAML pipelines", "Automated builds", "Automated testing", "CI/CD", "IIS deployment",
            "Docker", "Release management"])
    ];

    public async Task<IReadOnlyList<DiscoveryQuestion>> QuestionsAsync(CandidateProfileSnapshot snapshot, CandidateIntelligence? intelligence,
        CancellationToken token)
    {
        var prior = await database.ExperienceDiscovery.AsNoTracking().ToListAsync(token);
        var answers = prior.Select(row => JsonSerializer.Deserialize<DiscoveryAnswer>(row.ResponseJson, Json)!)
            .ToDictionary(item => Key(item.Skill));
        var known = (snapshot.Profile.TechnicalSkills ?? []).Concat((snapshot.Profile.CareerEvidence ?? []).Where(item =>
            item.Verification != EvidenceVerification.Unverified && item.SourceSection != DiscoverySection).Select(item => item.OriginalName))
            .Select(normalizer.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return QuestionCatalog(intelligence).Concat(answers.Values.Select(answer => (answer.Group, answer.Skill)))
            .DistinctBy(item => Key(item.Skill))
            .Where(item => answers.ContainsKey(Key(item.Skill)) || !known.Contains(normalizer.Normalize(item.Skill)))
            .Select(item => new DiscoveryQuestion(item.Group, item.Skill, answers.GetValueOrDefault(Key(item.Skill))))
            .ToArray();
    }

    public async Task<bool> SaveAsync(CandidateProfileSnapshot snapshot, CandidateIntelligence? intelligence,
        IReadOnlyList<DiscoveryAnswer> answers, CancellationToken token)
    {
        var catalog = (await QuestionsAsync(snapshot, intelligence, token)).ToDictionary(item => Key(item.Skill));
        if (answers.Count is < 1 or > 50 || answers.Any(item => item is null || string.IsNullOrWhiteSpace(item.Skill)) ||
            answers.Select(item => Key(item.Skill)).Distinct().Count() != answers.Count)
            throw new ArgumentException("Select between 1 and 50 different questions to confirm.");

        var normalized = answers.Select(item =>
        {
            if (!catalog.TryGetValue(Key(item.Skill), out var question) ||
                item.Level is not ("professional" or "personal_project" or "familiar" or "learning" or "none") ||
                item.Explanation?.Length > 1000)
                throw new ArgumentException("One or more discovery answers are invalid.");
            var details = item.Explanation?.Trim();
            if ((item.Level is "professional" or "personal_project") && (details is null || details.Length < 12))
                throw new ArgumentException("Describe how you used each skill you are confirming.");
            var employer = item.Level == "professional" ? ValidateAssociation(item.Employer,
                snapshot.Profile.Experiences.Select(role => role.Company)) : null;
            var project = item.Level is "professional" or "personal_project" ? ValidateAssociation(item.Project,
                (snapshot.Profile.ProjectDetails ?? []).Select(entry => entry.Name).Concat(snapshot.Profile.Projects)) : null;
            return new DiscoveryAnswer(question.Skill, question.Group, item.Level, details, employer, project);
        }).ToArray();
        var keys = normalized.Select(item => Key(item.Skill)).ToArray();
        var existing = await database.ExperienceDiscovery.Where(row => keys.Contains(row.SkillKey)).ToDictionaryAsync(row => row.SkillKey, token);

        var saved = await profiles.MergeImportAsync(snapshot.Version, profile =>
        {
            foreach (var item in normalized)
            {
                var key = Key(item.Skill);
                if (!existing.TryGetValue(key, out var row))
                    database.ExperienceDiscovery.Add(row = new ExperienceDiscoveryRecord { SkillKey = key });
                row.ResponseJson = JsonSerializer.Serialize(item, Json);
                var skill = normalizer.Normalize(item.Skill);
                var prior = profile.CareerEvidence.FirstOrDefault(evidence => evidence.SourceSection == DiscoverySection &&
                    evidence.NormalizedName == skill);
                if (prior is not null)
                    profile.CareerEvidence.Remove(prior);
                if (item.Level is "professional" or "personal_project")
                    profile.CareerEvidence.Add(new(prior?.Id ?? Guid.NewGuid(), item.Skill, skill, "technology",
                        EvidenceClassification.Unsupported, EvidenceVerification.ManuallyConfirmed, item.Employer, item.Project,
                        item.Explanation!, DiscoverySection, DateTimeOffset.UtcNow, ExperienceScope: item.Level));
            }
            return profile;
        }, token);
        return saved is not null;
    }

    private static string? ValidateAssociation(string? value, IEnumerable<string> saved)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return saved.FirstOrDefault(item => item.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ??
            throw new ArgumentException("Choose a saved employer or project for the evidence association.");
    }

    private static IEnumerable<(string Group, string Skill)> QuestionCatalog(CandidateIntelligence? intelligence) =>
        Catalog.SelectMany(group => group.Skills.Select(skill => (group.Group, skill)))
            .Concat(intelligence?.Competencies.Where(item => item.AssessmentStatus == "needs_verification")
                .Select(item => (item.Category, item.Name)) ?? []);

    internal static bool IsSpecificTechnique(string name) => Catalog.Any(group =>
        group.Skills.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase));

    private string Key(string skill) => JobRequirementCache.Key(normalizer.Normalize(skill));
}

internal sealed record DiscoveryQuestion(string Group, string Skill, DiscoveryAnswer? Answer);
internal sealed record DiscoveryAnswer(string Skill, string Group, string Level, string? Explanation, string? Employer, string? Project);
