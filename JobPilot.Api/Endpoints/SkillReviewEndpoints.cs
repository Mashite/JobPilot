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
        if (!request.Confirmed || request.ConfirmedSkills is not { } skills ||
            !CandidateProfileService.AreValidSkills(skills))
        {
            return Results.BadRequest("Explicitly confirm between 1 and 100 valid skills of at most 120 characters.");
        }

        if (request.Analysis is not { Requirements: { Count: > 0 and <= 500 } requirements } analysis ||
            requirements.Any(requirement => requirement is null || string.IsNullOrWhiteSpace(requirement.Skill)))
        {
            return Results.BadRequest("Analyze the job before reviewing its skills.");
        }

        var requirementKeys = requirements.Select(requirement => normalizer.Normalize(requirement.Skill))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (skills.Any(skill => !requirementKeys.Contains(normalizer.Normalize(skill))))
            return Results.BadRequest("Confirm only skills in the analyzed job requirements.");

        try
        {
            var profile = save
                ? await profiles.AddVerifiedSkillsAsync(skills, cancellationToken)
                : await profiles.GetProfileAsync(cancellationToken);
            return Results.Ok(matching.Match(analysis, profile, save ? null : skills));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: save
                    ? "The master profile could not be saved. Check that the profile file is writable and try again."
                    : "The master profile could not be read. Try again.");
        }
    }

    private sealed record SkillReviewRequest(JobAnalysis? Analysis, List<string>? ConfirmedSkills, bool Confirmed);
}
