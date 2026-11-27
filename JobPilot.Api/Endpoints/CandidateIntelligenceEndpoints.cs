using JobPilot.Api.Services;
using JobPilot.Api.Services.Ai;

namespace JobPilot.Api.Endpoints;

internal static class CandidateIntelligenceEndpoints
{
    public static void MapCandidateIntelligence(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup("/api/profile/intelligence")
            .AddEndpointFilter(ProfileEndpoints.RequireLocalProfileWriter).AddEndpointFilter(HandleErrors);
        routes.MapGet("", Get);
        routes.MapPost("/evaluate", Evaluate);
        routes.MapPost("/discovery", Save);
    }

    private static async Task<IResult> Get(CandidateProfileService profiles, CandidateEvaluationService evaluation,
        ExperienceDiscoveryService discovery, CancellationToken token)
    {
        var snapshot = await profiles.GetSnapshotAsync(token);
        var intelligence = await evaluation.ReadAsync(snapshot, token);
        return Results.Ok(new
        {
            profileVersion = snapshot.Version, snapshot.Profile.ProfessionalTitle,
            intelligence, questions = await discovery.QuestionsAsync(snapshot, intelligence, token),
            employers = snapshot.Profile.Experiences.Select(role => role.Company).Distinct().ToArray(),
            projects = (snapshot.Profile.ProjectDetails ?? []).Select(project => project.Name)
                .Concat(snapshot.Profile.Projects).Distinct().ToArray(),
            userConfirmed = snapshot.Profile.CareerEvidence.Where(item => item.Verification == JobPilot.Api.Models.EvidenceVerification.ManuallyConfirmed)
                .Select(item => new { name = item.OriginalName, item.Employer, item.Project, item.SourceExcerpt, item.ExperienceScope }).ToArray()
        });
    }

    private static async Task<IResult> Evaluate(CandidateProfileService profiles, CandidateEvaluationService evaluation, CancellationToken token) =>
        Results.Ok(await evaluation.EvaluateAsync(await profiles.GetSnapshotAsync(token), token));

    private static async Task<IResult> Save(DiscoveryConfirmation request, CandidateProfileService profiles,
        CandidateEvaluationService evaluation, ExperienceDiscoveryService discovery, CancellationToken token)
    {
        if (!request.Confirmed || request.Answers is null)
            return Results.BadRequest("Explicitly confirm the selected discovery answers.");
        var snapshot = await profiles.GetSnapshotAsync(token);
        if (snapshot.Version != request.ProfileVersion)
            return Results.Conflict("Master CV changed. Reload the page and confirm your answers again.");
        var intelligence = await evaluation.ReadAsync(snapshot, token);
        try
        {
            return await discovery.SaveAsync(snapshot, intelligence, request.Answers, token)
                ? Results.NoContent() : Results.Conflict("Master CV changed. Reload the page and confirm your answers again.");
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    private static async ValueTask<object?> HandleErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (AiProviderException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, title: "AI request failed", detail: ex.Message);
        }
        catch (Exception ex) when (ProfileEndpoints.IsStorageError(ex))
        {
            return Results.Problem(statusCode: 503, title: "Candidate intelligence storage is unavailable.",
                detail: "Check the candidate intelligence migration and database connection.");
        }
    }

    private sealed record DiscoveryConfirmation(string ProfileVersion, bool Confirmed, List<DiscoveryAnswer>? Answers);
}
