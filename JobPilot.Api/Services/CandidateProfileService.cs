using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

public class CandidateProfileService(IWebHostEnvironment environment, IConfiguration configuration, SkillNormalizer normalizer)
{
    private static readonly JsonSerializerOptions ProfileJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim writer = new(1);
    private readonly string path = Path.GetFullPath(
        configuration["CandidateProfile:Path"] ?? Path.Combine("Data", "candidate-profile.json"),
        environment.ContentRootPath);

    public async Task<CandidateProfile> GetProfileAsync(CancellationToken cancellationToken = default) =>
        (await GetSnapshotAsync(cancellationToken)).Profile;

    public async Task<CandidateProfileSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var (document, version) = await ReadDocumentAsync(cancellationToken);
        return new(ReadProfile(document), version);
    }

    public Task<CandidateProfileSnapshot?> SaveProfileAsync(
        CandidateProfile profile, string expectedVersion, CancellationToken cancellationToken = default) =>
        WithWriteLockAsync<CandidateProfileSnapshot?>(async () =>
        {
            var (document, version) = await ReadDocumentAsync(cancellationToken);
            if (!string.Equals(version, expectedVersion, StringComparison.Ordinal))
                return null;

            profile.AdditionalFields = ReadProfile(document).AdditionalFields;
            profile.Summary ??= "";
            profile.TechnicalSkills = NormalizeSkills(profile.TechnicalSkills);
            if (profile.EducationDetails is { } education)
                profile.Education = education.Select(FormatEducation).ToList();
            if (profile.ProjectDetails is { } projects)
                profile.Projects = projects.Select(FormatProject).ToList();

            foreach (var experience in profile.Experiences.Where(experience => experience.IsCurrentPosition))
                experience.EndDate = "Present";

            var updated = JsonSerializer.SerializeToNode(profile, ProfileJson)!;
            var savedVersion = await ReplaceProfileAsync(updated, cancellationToken);
            return new CandidateProfileSnapshot(profile, savedVersion);
        }, cancellationToken);

    public Task<CandidateProfile> AddVerifiedSkillsAsync(
        IReadOnlyCollection<string> confirmedSkills, CancellationToken cancellationToken = default)
    {
        if (confirmedSkills.Count is 0 or > 100 || !confirmedSkills.All(IsValidSkill))
            throw new ArgumentException("Confirm between 1 and 100 valid skill names of at most 120 characters.", nameof(confirmedSkills));

        return WithWriteLockAsync(async () =>
        {
            var (document, _) = await ReadDocumentAsync(cancellationToken);
            var profile = ReadProfile(document);
            var skills = NormalizeSkills((profile.TechnicalSkills ?? []).Concat(confirmedSkills));
            if (profile.TechnicalSkills is not null && profile.TechnicalSkills.SequenceEqual(skills))
                return profile;

            var propertyName = document.Select(property => property.Key)
                .SingleOrDefault(key => key.Equals(nameof(CandidateProfile.TechnicalSkills), StringComparison.OrdinalIgnoreCase))
                ?? "technicalSkills";
            document[propertyName] = JsonSerializer.SerializeToNode(skills, ProfileJson);
            await ReplaceProfileAsync(document, cancellationToken);
            profile.TechnicalSkills = skills;
            return profile;
        }, cancellationToken);
    }

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
