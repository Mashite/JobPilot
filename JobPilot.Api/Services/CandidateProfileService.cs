using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobPilot.Api.Models;
using JobPilot.Api.Data;
using JobPilot.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace JobPilot.Api.Services;

public class CandidateProfileService(IWebHostEnvironment environment, IConfiguration configuration, SkillNormalizer normalizer)
{
    private static readonly JsonSerializerOptions ProfileJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly JobPilotDbContext? database;
    private readonly SemaphoreSlim writer = new(1);
    private readonly string path = Path.GetFullPath(
        configuration["CandidateProfile:Path"] ?? Path.Combine("Data", "candidate-profile.json"),
        environment.ContentRootPath);

    internal CandidateProfileService(IWebHostEnvironment environment, IConfiguration configuration, SkillNormalizer normalizer,
        JobPilotDbContext database) : this(environment, configuration, normalizer) => this.database = database;

    public async Task<CandidateProfile> GetProfileAsync(CancellationToken cancellationToken = default) =>
        (await GetSnapshotAsync(cancellationToken)).Profile;

    public async Task<CandidateProfileSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (database is not null)
        {
            var stored = await database.CareerProfiles.AsNoTracking().SingleOrDefaultAsync(row => row.Id == 1, cancellationToken);
            if (stored is not null)
                return new(ReadStoredProfile(stored), stored.Version);
            return await ReadLegacySnapshotAsync(cancellationToken);
        }

        var (document, version) = await ReadDocumentAsync(cancellationToken);
        return new(ReadProfile(document), version);
    }

    public Task<CandidateProfileSnapshot?> SaveProfileAsync(
        CandidateProfile profile, string expectedVersion, CancellationToken cancellationToken = default) =>
        database is not null
        ? EditDatabaseAsync(expectedVersion, current => PrepareProfile(ApplyEvidenceEdits(profile, current), current), cancellationToken)
        : WithWriteLockAsync<CandidateProfileSnapshot?>(async () =>
        {
            var (document, version) = await ReadDocumentAsync(cancellationToken);
            if (!string.Equals(version, expectedVersion, StringComparison.Ordinal))
                return null;

            var current = ReadProfile(document);
            PrepareProfile(ApplyEvidenceEdits(profile, current), current);

            var updated = JsonSerializer.SerializeToNode(profile, ProfileJson)!;
            var savedVersion = await ReplaceProfileAsync(updated, cancellationToken);
            return new CandidateProfileSnapshot(profile, savedVersion);
        }, cancellationToken);

    public async Task<CandidateProfile> AddVerifiedSkillsAsync(
        IReadOnlyCollection<string> confirmedSkills, CancellationToken cancellationToken = default,
        IReadOnlyCollection<CareerEvidence>? evidence = null)
    {
        if ((confirmedSkills.Count == 0 && evidence is not { Count: > 0 }) ||
            confirmedSkills.Count > 100 || !confirmedSkills.All(IsValidSkill))
            throw new ArgumentException("Confirm between 1 and 100 valid skill names of at most 120 characters.", nameof(confirmedSkills));

        if (database is not null)
        {
            var saved = await EditDatabaseAsync(null, profile =>
            {
                profile.TechnicalSkills = NormalizeSkills((profile.TechnicalSkills ?? []).Concat(confirmedSkills));
                if (evidence is not null)
                    AddEvidence(profile, AssociateEvidence(profile, evidence));
                return profile;
            }, cancellationToken);
            return saved!.Profile;
        }

        return await WithWriteLockAsync(async () =>
        {
            var (document, _) = await ReadDocumentAsync(cancellationToken);
            var profile = ReadProfile(document);
            var skills = NormalizeSkills((profile.TechnicalSkills ?? []).Concat(confirmedSkills));
            if (evidence is null && profile.TechnicalSkills is not null && profile.TechnicalSkills.SequenceEqual(skills))
                return profile;

            var propertyName = document.Select(property => property.Key)
                .SingleOrDefault(key => key.Equals(nameof(CandidateProfile.TechnicalSkills), StringComparison.OrdinalIgnoreCase))
                ?? "technicalSkills";
            document[propertyName] = JsonSerializer.SerializeToNode(skills, ProfileJson);
            if (evidence is not null)
            {
                AddEvidence(profile, AssociateEvidence(profile, evidence));
                document["careerEvidence"] = JsonSerializer.SerializeToNode(profile.CareerEvidence, ProfileJson);
            }
            await ReplaceProfileAsync(document, cancellationToken);
            profile.TechnicalSkills = skills;
            return profile;
        }, cancellationToken);
    }

    internal Task<CandidateProfileSnapshot?> MergeImportAsync(
        string version, Func<CandidateProfile, CandidateProfile> merge, CancellationToken cancellationToken)
    {
        if (database is null)
            throw new InvalidOperationException("The Master CV import requires the database.");
        return EditDatabaseAsync(version, current => PrepareProfile(merge(current), current), cancellationToken);
    }

    private CandidateProfile PrepareProfile(CandidateProfile profile, CandidateProfile current)
    {
        profile.AdditionalFields = current.AdditionalFields;
        profile.Summary ??= "";
        profile.CareerEvidence ??= [];
        profile.TechnicalSkills = NormalizeSkills(profile.TechnicalSkills);
        if (profile.EducationDetails is { } education)
            profile.Education = education.Select(FormatEducation).ToList();
        if (profile.ProjectDetails is { } projects)
            profile.Projects = projects.Select(FormatProject).ToList();
        foreach (var experience in profile.Experiences.Where(experience => experience.IsCurrentPosition &&
            string.IsNullOrWhiteSpace(experience.EndDate)))
            experience.EndDate = "Present";
        return profile;
    }

    private static CandidateProfile ApplyEvidenceEdits(CandidateProfile profile, CandidateProfile current)
    {
        var edits = (profile.CareerEvidence ?? []).Where(item => item is not null).DistinctBy(item => item.Id)
            .ToDictionary(item => item.Id, item => item.Verification);
        profile.CareerEvidence = (current.CareerEvidence ?? []).Select(item =>
        {
            if (!edits.TryGetValue(item.Id, out var verification) || !Enum.IsDefined(verification) ||
                verification == item.Verification ||
                (verification == EvidenceVerification.ImportConfirmed && item.Classification != EvidenceClassification.ExplicitlyDocumented))
                return item;
            return item with { Verification = verification, UpdatedAt = DateTimeOffset.UtcNow };
        }).ToList();
        profile.ImportReviewSections = current.ImportReviewSections ?? [];
        return profile;
    }

    private static IEnumerable<CareerEvidence> AssociateEvidence(CandidateProfile profile, IEnumerable<CareerEvidence> items)
    {
        foreach (var item in items)
        {
            var employer = profile.Experiences.FirstOrDefault(role =>
                string.Equals(role.Company, item.SourceExcerpt, StringComparison.OrdinalIgnoreCase) ||
                role.Responsibilities.Concat(role.Achievements).Contains(item.SourceExcerpt, StringComparer.OrdinalIgnoreCase));
            var project = profile.ProjectDetails?.FirstOrDefault(project =>
                string.Equals(project.Name, item.SourceExcerpt, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(project.Description, item.SourceExcerpt, StringComparison.OrdinalIgnoreCase));
            yield return item with { Employer = employer?.Company, Project = project?.Name };
        }
    }

    internal static void AddEvidence(CandidateProfile profile, IEnumerable<CareerEvidence> additions)
    {
        foreach (var item in additions)
        {
            var index = profile.CareerEvidence.FindIndex(existing =>
                existing.NormalizedName == item.NormalizedName && existing.Employer == item.Employer &&
                existing.Project == item.Project && existing.SourceExcerpt == item.SourceExcerpt &&
                existing.SourceSection == item.SourceSection);
            if (index < 0)
                profile.CareerEvidence.Add(item);
            else if ((profile.CareerEvidence[index].Verification == EvidenceVerification.Unverified &&
                item.Verification != EvidenceVerification.Unverified) ||
                (item.Verification == EvidenceVerification.ManuallyConfirmed &&
                 item.ConfirmedExperienceMonths.GetValueOrDefault() > profile.CareerEvidence[index].ConfirmedExperienceMonths.GetValueOrDefault()))
                profile.CareerEvidence[index] = item with { Id = profile.CareerEvidence[index].Id };
        }
    }

    private async Task<CandidateProfileSnapshot?> EditDatabaseAsync(
        string? expectedVersion, Func<CandidateProfile, CandidateProfile> edit, CancellationToken cancellationToken)
    {
        var db = database!;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var query = db.Database.IsSqlServer()
            ? db.CareerProfiles.FromSqlRaw("SELECT * FROM [CareerProfiles] WITH (UPDLOCK, HOLDLOCK)")
            : db.CareerProfiles;
        var row = await query.SingleOrDefaultAsync(record => record.Id == 1, cancellationToken);
        var snapshot = row is null ? await ReadLegacySnapshotAsync(cancellationToken) : new(ReadStoredProfile(row), row.Version);
        if (expectedVersion is not null && expectedVersion != snapshot.Version)
            return null;
        var updated = edit(snapshot.Profile);
        if (row is null)
        {
            row = new CareerProfileRecord { Id = 1 };
            db.CareerProfiles.Add(row);
        }
        row.ProfileJson = JsonSerializer.Serialize(updated, ProfileJson);
        row.Version = GetVersion(Encoding.UTF8.GetBytes(row.ProfileJson));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(updated, row.Version);
    }

    private async Task<CandidateProfileSnapshot> ReadLegacySnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (document, version) = await ReadDocumentAsync(cancellationToken);
            return new(ReadProfile(document), version);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(new CandidateProfile(), "\"empty-profile\"");
        }
    }

    private static CandidateProfile ReadStoredProfile(CareerProfileRecord stored) =>
        JsonSerializer.Deserialize<CandidateProfile>(stored.ProfileJson, ProfileJson)
        ?? throw new InvalidOperationException("Candidate profile is empty.");

    internal static bool IsValidSkill(string? skill) =>
        !string.IsNullOrWhiteSpace(skill) && skill.Length <= 120 &&
        !skill.Any(char.IsControl) && skill.Any(char.IsLetterOrDigit);

    internal static bool AreValidSkills(IReadOnlyCollection<string> skills) =>
        skills.Count is > 0 and <= 100 && skills.All(IsValidSkill);

    private List<string> NormalizeSkills(IEnumerable<string> skills) => skills
        .Where(skill => !string.IsNullOrWhiteSpace(skill))
        .Select(normalizer.CleanDisplayName)
        .DistinctBy(normalizer.Normalize, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private async Task<T> WithWriteLockAsync<T>(Func<Task<T>> edit, CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            // The filesystem lock also prevents conflicting writes from another API process.
            await using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            return await edit();
        }
        finally
        {
            writer.Release();
        }
    }

    private async Task<(JsonObject Document, string Version)> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        var document = JsonNode.Parse(bytes.AsSpan(offset)) as JsonObject
            ?? throw new InvalidOperationException("Candidate profile is invalid.");
        return (document, GetVersion(bytes));
    }

    private static CandidateProfile ReadProfile(JsonObject document) =>
        document.Deserialize<CandidateProfile>(ProfileJson)
        ?? throw new InvalidOperationException("Candidate profile is empty.");

    private static string GetVersion(byte[] bytes) => $"\"{Convert.ToHexString(SHA256.HashData(bytes))}\"";

    private async Task<string> ReplaceProfileAsync(JsonNode document, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ProfileJson);
        var version = GetVersion(bytes);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(temporaryPath, path, destinationBackupFileName: null);
            return version;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string FormatEducation(ProfileEducation education) =>
        JoinDetails(education.Degree, education.Institution, education.GraduationDate, education.Details);

    private static string FormatProject(ProfileProject project) =>
        JoinDetails(project.Name, project.Description, string.Join(", ", project.Technologies),
            string.Join("; ", project.Achievements), project.Url);

    private static string JoinDetails(params string?[] values) =>
        string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record CandidateProfileSnapshot(CandidateProfile Profile, string Version);
