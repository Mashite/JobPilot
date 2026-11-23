using Azure.Core;
using JobPilot.Api.Models.Agent;
using JobPilot.Api.Models.Resume;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Agent;

namespace JobPilot.Api.Endpoints;

internal static class JobPilotEndpoints
{
    public static void MapJobPilotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/jobs/analyze", AnalyzeJob);
        endpoints.MapPost("/api/jobs/match", MatchJob);
        endpoints.MapPost("/api/resumes/generate", GenerateResume);
        endpoints.MapPost("/api/resumes/generate-docx", GenerateResumeDocx);
        endpoints.MapPost("/api/resumes/export-docx", ExportResumeDocx);
        endpoints.MapPost("/api/agent/chat", ChatWithAgent);
    }

    private static async Task<IResult> AnalyzeJob(
        AnalyzeJobRequest request,
        OllamaService ollama,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobDescription))
            return Results.BadRequest("Job description is required.");

        var result = await ollama.AnalyzeJobAsync(request.JobDescription, cancellationToken);
        return Results.Ok(new { analysis = result });
    }

    private static async Task<IResult> MatchJob(
        AnalyzeJobRequest request,
        CandidateProfileService profileService,
        OllamaService ollamaService,
        JobMatchingService matchingService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobDescription))
            return Results.BadRequest("Job description is required.");

        var profile = await profileService.GetProfileAsync(cancellationToken);
        var jobAnalysis = await ollamaService.ExtractRequirementsAsync(
            request.JobDescription, cancellationToken);
        var matchResult = matchingService.Match(jobAnalysis, profile);
        return Results.Ok(matchResult);
    }

    private static async Task<IResult> GenerateResume(
        AnalyzeJobRequest request,
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

        var resume = await resumeGenerator.GenerateAsync(request.JobDescription, profile, cancellationToken);
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

        try
        {
            var result = await agent.RunAsync(
                request,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: 502);
        }
    }
}
