using System.Text.Json;
using JobPilot.Api.Models;
using JobPilot.Api.Services;

namespace JobPilot.Api.Endpoints;

internal static class SkillReviewEndpoints
{
    public static void MapSkillReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/jobs/skill-review/preview", Preview);
        endpoints.MapPost("/api/profile/skills", Save)
            .AddEndpointFilter(ProfileEndpoints.RequireLocalProfileWriter);
    }

    private static Task<IResult> Preview(SkillReviewRequest request, SkillNormalizer normalizer,
        CandidateProfileService profiles, JobMatchingService matching, CancellationToken cancellationToken) =>
        ReviewAsync(request, normalizer, profiles, matching, save: false, cancellationToken);

    private static Task<IResult> Save(SkillReviewRequest request, SkillNormalizer normalizer,
        CandidateProfileService profiles, JobMatchingService matching, CancellationToken cancellationToken) =>
        ReviewAsync(request, normalizer, profiles, matching, save: true, cancellationToken);

    private static async Task<IResult> ReviewAsync(SkillReviewRequest request, SkillNormalizer normalizer,
        CandidateProfileService profiles, JobMatchingService matching, bool save, CancellationToken cancellationToken)
    {
        if (!request.Confirmed || request.ConfirmedSkills is not { Count: > 0 and <= 100 } skills ||
            skills.Any(skill => string.IsNullOrWhiteSpace(skill) || skill.Length > 4_000))
        {
            return Results.BadRequest("Explicitly confirm between 1 and 100 analyzed requirements.");
        }

        if (request.Analysis is not { Requirements: { Count: > 0 and <= 500 } requirements } analysis ||
            requirements.Any(requirement => requirement is null || string.IsNullOrWhiteSpace(requirement.Skill)))
        {
            return Results.BadRequest("Analyze the job before reviewing its skills.");
        }

        var byKey = matching.GetDistinctRequirements(analysis)
            .ToDictionary(requirement => normalizer.Normalize(requirement.Skill), StringComparer.OrdinalIgnoreCase);
        var keys = skills.Select(normalizer.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Any(key => !byKey.ContainsKey(key)))
            return Results.BadRequest("Confirm only analyzed job requirements.");
        if (save && (!CandidateProfileService.AreValidSkills(skills) ||
            keys.Any(key => !JobMatchingService.CanSaveAsProfileSkill(byKey[key]))))
        {
            return Results.BadRequest("Only verified skills can be saved here. Edit qualifications in Master CV.");
        }

        var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, note) in request.ConfirmedEvidence ?? [])
        {
            var key = normalizer.Normalize(name);
            if (!keys.Contains(key) || note is null || note.Length > 1000 || !evidence.TryAdd(key, note.Trim()))
                return Results.BadRequest("Provide one evidence note of at most 1,000 characters per confirmed requirement.");
        }
        if (keys.Any(key => JobMatchingService.NeedsReviewEvidence(byKey[key]) &&
            string.IsNullOrWhiteSpace(evidence.GetValueOrDefault(key))))
        {
            return Results.BadRequest("Cite supporting Master CV evidence for qualifications and minimum experience.");
        }

        try
        {
            var profile = save
                ? await profiles.AddVerifiedSkillsAsync(skills, cancellationToken)
                : await profiles.GetProfileAsync(cancellationToken);
            return Results.Ok(matching.Match(analysis, profile, save ? null : skills, save ? null : evidence));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: save
                    ? "The master profile could not be saved. Check that the profile file is writable and try again."
                    : "The master profile could not be read. Try again.");
        }
    }

    private sealed record SkillReviewRequest(
        JobAnalysis? Analysis, List<string>? ConfirmedSkills, bool Confirmed,
        Dictionary<string, string?>? ConfirmedEvidence = null);
}
