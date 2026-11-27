using System.Text.Json;
using JobPilot.Api.Models;
using JobPilot.Api.Models.Agent;
using JobPilot.Api.Models.Resume;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Agent;
using JobPilot.Api.Services.Ai;

namespace JobPilot.Api.Endpoints;

internal static class JobPilotEndpoints
{
    public static void MapJobPilotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/jobs/analyze", AnalyzeJob).AddEndpointFilter(HandleAiErrors);
        endpoints.MapPost("/api/jobs/match", MatchJob).AddEndpointFilter(HandleAiErrors);
        endpoints.MapPost("/api/resumes/generate", GenerateResume).AddEndpointFilter(HandleAiErrors);
        endpoints.MapPost("/api/resumes/generate-docx", GenerateResumeDocx).AddEndpointFilter(HandleAiErrors);
        endpoints.MapPost("/api/resumes/export-docx", ExportResumeDocx);
        endpoints.MapPost("/api/agent/chat", ChatWithAgent).AddEndpointFilter(HandleAiErrors);
    }

    private static async ValueTask<object?> HandleAiErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (AiProviderException ex)
        {
            return Results.Problem(title: "AI request failed", detail: ex.Message, statusCode: ex.StatusCode,
                extensions: new Dictionary<string, object?>
                {
                    ["providerStatusCode"] = ex.ProviderStatusCode,
                    ["diagnosticCode"] = ex.DiagnosticCode
                });
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return ProfileEndpoints.StorageUnavailable();
        }
    }

    private static async Task<IResult> AnalyzeJob(
        AnalyzeJobRequest request,
        JobAnalysisService analysis,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobDescription))
            return Results.BadRequest("Job description is required.");

        var result = await analysis.AnalyzeJobAsync(request.JobDescription, cancellationToken);
        return Results.Ok(new { analysis = result });
    }

    private static async Task<IResult> MatchJob(
        AnalyzeJobRequest request,
        HttpContext context,
        CandidateProfileService profileService,
        JobAnalysisService analysis,
        JobMatchingService matchingService,
        AiProviderSelection activeProvider,
        ILogger<JobMatchingService> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobDescription))
            return Results.BadRequest("Job description is required.");

        var stage = "profile_load";
        string? errorType = null;
        int? verifiedCount = null;
        JobAnalysis? extracted = null;
        JobMatchResult? match = null;
        try
        {
            var snapshot = await profileService.GetSnapshotAsync(cancellationToken);
            var profile = snapshot.Profile;
            verifiedCount = profile.TechnicalSkills?.Count(skill => !string.IsNullOrWhiteSpace(skill)) ?? 0;
            stage = "extraction";
            extracted = await analysis.ExtractRequirementsAsync(request.JobDescription, cancellationToken);
            stage = "matching";
            var semantic = context.RequestServices.GetService<SemanticJobMatcher>();
            if (semantic is null)
                match = matchingService.Match(extracted, profile);
            else
                match = await JobMatchingWorkflow.MatchAsync(request.JobDescription, extracted, snapshot, semantic,
                    context.RequestServices.GetService<JobMatchReviewStore>(), logger, cancellationToken);
            stage = "completed";
            return Results.Ok(match);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            if (stage == "profile_load" && ProfileEndpoints.IsStorageError(ex))
            {
                return Results.Problem(title: "Master CV could not be loaded",
                    detail: "Open Master CV and check the configured profile storage.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            throw;
        }
        finally
        {
            logger.Log(errorType is null ? LogLevel.Information : LogLevel.Warning,
                "Job match provider {Provider} model {Model} stage {Stage} extraction succeeded {ExtractionSucceeded} extracted requirements {ExtractedRequirements} normalized requirements {NormalizedRequirements} verified profile skills {VerifiedSkills} normalized verified skills {NormalizedVerifiedSkills} exact {ExactMatches} equivalent {EquivalentMatches} related {RelatedMatches} missing {MissingRequirements} score {MatchScore} error type {ErrorType}",
                activeProvider.Name, activeProvider.Model, stage, extracted is not null, extracted?.ExtractedItemCount,
                match?.Requirements.Count, match?.VerifiedSkillCount ?? verifiedCount, match?.NormalizedVerifiedSkillCount,
                match?.RequirementMatches.Count(item => item.Status == "exact"),
                match?.RequirementMatches.Count(item => item.Status == "equivalent"),
                match?.RequirementMatches.Count(item => item.Status == "related"),
                match?.RequirementMatches.Count(item => item.Status == "missing"), match?.MatchScore, errorType);
            if (match?.SemanticStatus is { } semanticStatus)
                logger.LogInformation("Semantic match status {Status}. Supported {SemanticMatches} partial {PartialMatches} needs verification {NeedsVerification}",
                    semanticStatus, match.RequirementMatches.Count(item => item.Status == "semantic"),
                    match.RequirementMatches.Count(item => item.Status == "partial"),
                    match.RequirementMatches.Count(item => item.Status == "needs_verification"));
        }
    }

    private static async Task<IResult> GenerateResume(
        GenerateResumeRequest request,
        CandidateProfileService profileService,
        CandidateProfileValidator validator,
        ResumeGeneratorService resumeGenerator,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobDescription))
            return Results.BadRequest("Job description is required.");

        var profile = await profileService.GetProfileAsync(cancellationToken);
        var errors = validator.Validate(profile);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["CandidateProfile"] = errors.ToArray()
            });
        }

        var resume = await resumeGenerator.GenerateAsync(
            request.JobDescription, profile, cancellationToken, request.ConfirmedSkills, request.ConfirmedEvidence,
            request.IncludeAllExperiences);
        return Results.Ok(resume);
    }

    private static async Task<IResult> GenerateResumeDocx(
        AnalyzeJobRequest request,
        CandidateProfileService profileService,
        CandidateProfileValidator validator,
        ResumeGeneratorService resumeGenerator,
        WordDocumentService wordService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobDescription))
            return Results.BadRequest("Job description is required.");

        var profile = await profileService.GetProfileAsync(cancellationToken);
        var errors = validator.Validate(profile);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["CandidateProfile"] = errors.ToArray()
            });
        }

        var resume = await resumeGenerator.GenerateAsync(request.JobDescription, profile, cancellationToken);
        var documentBytes = wordService.Generate(resume);
        return Results.File(
            documentBytes,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "TailoredResume.docx");
    }

    private static IResult ExportResumeDocx(GeneratedResume resume, WordDocumentService wordService)
    {
        if (string.IsNullOrWhiteSpace(resume.FullName))
            return Results.BadRequest("Resume full name is required.");

        var bytes = wordService.Generate(resume);
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "JobPilotResume.docx");
    }



    private static async Task<IResult> ChatWithAgent(AgentRequest request,
        JobPilotAgentService agent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.BadRequest(
                new { error = "Message is required." });
        }

        return Results.Ok(await agent.RunAsync(request, cancellationToken));
    }
}
