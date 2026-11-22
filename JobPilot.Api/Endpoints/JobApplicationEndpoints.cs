using System.Text.Json;
using JobPilot.Api.Data;
using JobPilot.Api.Data.Entities;
using JobPilot.Api.Models.Resume;
using Microsoft.EntityFrameworkCore;

namespace JobPilot.Api.Endpoints;

internal static class JobApplicationEndpoints
{
    private static readonly HashSet<string> Statuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Interested", "Applied", "Interviewing", "Offered", "Rejected", "Withdrawn"
        };

    public static void MapJobApplicationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var applications = endpoints.MapGroup("/api/applications");
        applications.MapGet("", ListApplications);
        applications.MapPost("", CreateApplication);
        applications.MapGet("/{id:int}", GetApplication);
        applications.MapPatch("/{id:int}/status", UpdateStatus);
        applications.MapPost("/{id:int}/resumes", SaveResume);
        applications.MapGet("/{id:int}/resumes/latest", GetLatestResume);
    }

    private static async Task<IResult> ListApplications(
        JobPilotDbContext db, CancellationToken cancellationToken)
    {
        var applications = await db.JobApplications.AsNoTracking()
            .OrderByDescending(job => job.CreatedAtUtc)
            .ThenByDescending(job => job.Id)
            .Select(job => new
            {
                job.Id, job.CompanyName, job.JobTitle, job.MatchScore, job.Status, job.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(applications);
    }

    private static async Task<IResult> CreateApplication(
        CreateApplicationRequest request, JobPilotDbContext db, CancellationToken cancellationToken)
    {
        var jobTitle = request.JobTitle?.Trim();
        var companyName = request.CompanyName?.Trim();
        var jobDescription = request.JobDescription?.Trim();
        if (string.IsNullOrEmpty(jobTitle) || jobTitle.Length > 300)
            return Results.BadRequest("Job title is required and must be at most 300 characters.");
        if (string.IsNullOrEmpty(companyName) || companyName.Length > 200)
            return Results.BadRequest("Company name is required and must be at most 200 characters.");
        if (string.IsNullOrEmpty(jobDescription))
            return Results.BadRequest("Job description is required.");
        if (request.MatchScore is < 0 or > 100)
            return Results.BadRequest("Match score must be between 0 and 100.");
        if (request.Resume is not null && string.IsNullOrWhiteSpace(request.Resume.FullName))
            return Results.BadRequest("Resume full name is required.");

        var application = new JobApplication
        {
            JobTitle = jobTitle,
            CompanyName = companyName,
            JobDescription = jobDescription,
            MatchScore = request.MatchScore
        };

        if (request.Resume is not null)
            application.Resumes.Add(CreateSavedResume(request.Resume));

        db.JobApplications.Add(application);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/applications/{application.Id}", ToDetails(application));
    }

    private static async Task<IResult> GetApplication(
        int id, JobPilotDbContext db, CancellationToken cancellationToken)
    {
        var application = await db.JobApplications.AsNoTracking()
            .SingleOrDefaultAsync(job => job.Id == id, cancellationToken);
        return application is null ? Results.NotFound() : Results.Ok(ToDetails(application));
    }

    private static async Task<IResult> UpdateStatus(
        int id, UpdateStatusRequest request, JobPilotDbContext db, CancellationToken cancellationToken)
    {
        if (!Statuses.TryGetValue(request.Status?.Trim() ?? "", out var status))
            return Results.BadRequest("Invalid application status.");

        var application = await db.JobApplications.FindAsync([id], cancellationToken);
        if (application is null)
            return Results.NotFound();

        application.Status = status;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveResume(
        int id, GeneratedResume resume, JobPilotDbContext db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resume.FullName))
            return Results.BadRequest("Resume full name is required.");

        if (!await db.JobApplications.AnyAsync(job => job.Id == id, cancellationToken))
            return Results.NotFound();

        var savedResume = CreateSavedResume(resume);
        savedResume.JobApplicationId = id;
        db.SavedResumes.Add(savedResume);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/applications/{id}/resumes/latest", new { savedResume.Id });
    }

    private static async Task<IResult> GetLatestResume(
        int id, JobPilotDbContext db, CancellationToken cancellationToken)
    {
        var resumeJson = await db.SavedResumes.AsNoTracking()
            .Where(resume => resume.JobApplicationId == id)
            .OrderByDescending(resume => resume.CreatedAtUtc)
            .ThenByDescending(resume => resume.Id)
            .Select(resume => resume.ResumeJson)
            .FirstOrDefaultAsync(cancellationToken);

        return resumeJson is null
            ? Results.NotFound()
            : Results.Content(resumeJson, "application/json");
    }

    private static SavedResume CreateSavedResume(GeneratedResume resume) => new()
    {
        ResumeJson = JsonSerializer.Serialize(resume, JsonSerializerOptions.Web)
    };

    private static ApplicationDetails ToDetails(JobApplication application) => new(
        application.Id, application.JobTitle, application.CompanyName, application.JobDescription,
        application.MatchScore, application.Status, application.CreatedAtUtc);

    private sealed record CreateApplicationRequest(
        string? JobTitle, string? CompanyName, string? JobDescription, int? MatchScore, GeneratedResume? Resume);

    private sealed record UpdateStatusRequest(string? Status);

    private sealed record ApplicationDetails(
        int Id, string JobTitle, string CompanyName, string JobDescription, int? MatchScore,
        string Status, DateTime CreatedAtUtc);
}
