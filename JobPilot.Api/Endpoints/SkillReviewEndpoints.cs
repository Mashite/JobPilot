using System.Text.Json;
using JobPilot.Api.Models;
using JobPilot.Api.Services;

namespace JobPilot.Api.Endpoints;

internal static class SkillReviewEndpoints
{
    public static void MapSkillReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/jobs/skill-review/preview", Preview);
        endpoints.MapPost("/api/jobs/skill-review/save", SaveJobReview)
            .AddEndpointFilter(ProfileEndpoints.RequireLocalProfileWriter);
        endpoints.MapPost("/api/profile/skills", Save)
            .AddEndpointFilter(ProfileEndpoints.RequireLocalProfileWriter);
    }

    private static Task<IResult> Preview(SkillReviewRequest request, HttpContext context, SkillNormalizer normalizer,
        CandidateProfileService profiles, JobMatchingService matching, CancellationToken cancellationToken) =>
        ReviewAsync(request, context, normalizer, profiles, matching, saveProfile: false, saveReview: false, cancellationToken);

    private static Task<IResult> Save(SkillReviewRequest request, HttpContext context, SkillNormalizer normalizer,
        CandidateProfileService profiles, JobMatchingService matching, CancellationToken cancellationToken) =>
        ReviewAsync(request, context, normalizer, profiles, matching, saveProfile: true, saveReview: false, cancellationToken);

    private static Task<IResult> SaveJobReview(SkillReviewRequest request, HttpContext context, SkillNormalizer normalizer,
        CandidateProfileService profiles, JobMatchingService matching, CancellationToken cancellationToken) =>
        ReviewAsync(request, context, normalizer, profiles, matching, saveProfile: false, saveReview: true, cancellationToken);

    private static async Task<IResult> ReviewAsync(SkillReviewRequest request, HttpContext context, SkillNormalizer normalizer,
        CandidateProfileService profiles, JobMatchingService matching, bool saveProfile, bool saveReview, CancellationToken cancellationToken)
    {
        var skills = request.ConfirmedSkills ?? [];
        var related = request.RelatedSkills ?? [];
        var missing = request.MissingSkills ?? [];
        if (!request.Confirmed || skills.Count + related.Count + missing.Count > 100 ||
            (saveProfile && (skills.Count == 0 || related.Count != 0 || missing.Count != 0)) ||
            skills.Concat(related).Concat(missing).Any(skill => string.IsNullOrWhiteSpace(skill) || skill.Length > 4_000))
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
        var relatedKeys = related.Select(normalizer.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingKeys = missing.Select(normalizer.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Overlaps(relatedKeys) || keys.Overlaps(missingKeys) || relatedKeys.Overlaps(missingKeys) ||
            keys.Concat(relatedKeys).Concat(missingKeys).Any(key => !byKey.ContainsKey(key)))
            return Results.BadRequest("Confirm only analyzed job requirements.");
        if (saveProfile && (!CandidateProfileService.AreValidSkills(skills) ||
            (!request.ConfirmCompetencies && keys.Any(key => !JobMatchingService.CanSaveAsProfileSkill(byKey[key])))))
        {
            return Results.BadRequest("Only verified skills can be saved here. Edit qualifications in Master CV.");
        }

        var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, note) in request.ConfirmedEvidence ?? [])
        {
            var key = normalizer.Normalize(name);
            if ((!keys.Contains(key) && !relatedKeys.Contains(key)) || note is null || note.Length > 1000 || !evidence.TryAdd(key, note.Trim()))
                return Results.BadRequest("Provide one evidence note of at most 1,000 characters per confirmed requirement.");
        }
        if (relatedKeys.Any(key => string.IsNullOrWhiteSpace(evidence.GetValueOrDefault(key))) ||
            keys.Any(key => (saveReview || request.ConfirmCompetencies || JobMatchingService.NeedsReviewEvidence(byKey[key])) &&
            string.IsNullOrWhiteSpace(evidence.GetValueOrDefault(key))))
        {
            return Results.BadRequest("Cite supporting experience for each reviewed competency. Include dates when minimum experience applies.");
        }

        try
        {
            var review = new JobReview(skills.ToArray(), related.ToArray(), missing.ToArray(), evidence);
            var semantic = context.RequestServices.GetService<SemanticJobMatcher>();
            if (saveReview && (string.IsNullOrWhiteSpace(request.JobDescription) || semantic is null ||
                context.RequestServices.GetService<JobMatchReviewStore>() is null))
                return Results.BadRequest("Supply the analyzed job listing to save review decisions.");

            var records = keys.Where(key => evidence.ContainsKey(key) && evidence[key].Length > 0)
                .Select(key => new CareerEvidence(Guid.NewGuid(), byKey[key].Skill, key, byKey[key].Category,
                    EvidenceClassification.Unsupported, EvidenceVerification.ManuallyConfirmed, null, null,
                    evidence[key], "Manual job review", DateTimeOffset.UtcNow, byKey[key].MinimumExperienceMonths)).ToList();
            var flatSkills = keys.Where(key => JobMatchingService.CanSaveAsProfileSkill(byKey[key]))
                .Select(key => byKey[key].Skill).ToList();
            if (saveProfile)
                await profiles.AddVerifiedSkillsAsync(flatSkills, cancellationToken, records.Count > 0 ? records : null);
            var snapshot = await profiles.GetSnapshotAsync(cancellationToken);
            if (semantic is null || string.IsNullOrWhiteSpace(request.JobDescription))
                return Results.Ok(matching.Match(analysis, snapshot.Profile, saveProfile ? null : skills,
                    saveProfile ? null : evidence, related, missing));

            var automatic = await semantic.MatchAsync(analysis, snapshot, cancellationToken, allowProvider: saveProfile);
            if (saveReview)
                await context.RequestServices.GetRequiredService<JobMatchReviewStore>()
                    .SaveAsync(request.JobDescription, automatic.Requirements, review, cancellationToken);
            return Results.Ok(saveProfile ? automatic : semantic.ApplyReview(automatic, snapshot.Profile, review));
        }
        catch (Exception ex) when (ProfileEndpoints.IsStorageError(ex))
        {
            if (saveReview)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Job review storage is unavailable.",
                    detail: JobMatchReviewStore.IsMissingTable(ex)
                        ? "Apply the AddJobMatchReviews database migration, then save the job review again."
                        : "Check the database connection and try saving the job review again.");
            }
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: saveProfile
                    ? "The master profile could not be saved. Check the database or retry the update."
                    : "The master profile could not be read. Try again.");
        }
    }

    private sealed record SkillReviewRequest(
        JobAnalysis? Analysis, List<string>? ConfirmedSkills, bool Confirmed,
        Dictionary<string, string?>? ConfirmedEvidence = null,
        List<string>? RelatedSkills = null, List<string>? MissingSkills = null, bool ConfirmCompetencies = false,
        string? JobDescription = null);
}
