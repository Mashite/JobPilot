using System.Data.Common;
using JobPilot.Api.Models.Agent;
using JobPilot.Api.Services.Agent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace JobPilot.Api.Endpoints;

internal static class AgentWorkflowEndpoints
{
    public static void MapAgentWorkflows(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/agent/workflows/{id:guid}")
            .AddEndpointFilter(ProfileEndpoints.RequireLocalProfileWriter)
            .AddEndpointFilter(HandleStorageError);
        group.MapPost("", async (Guid id, AgentRequest request, AgentWorkflowService workflows, CancellationToken token) =>
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 8_000 ||
                string.IsNullOrWhiteSpace(request.JobDescription) || request.JobDescription.Length > 80_000)
                return Results.BadRequest("Supply a message and job listing within the input limits.");

            var result = await workflows.RunAsync(id, request.Message.Trim(), request.JobDescription.Trim(), token);
            return result is null ? Results.Conflict("This workflow is already running or belongs to another request.") : Results.Ok(result);
        });
        group.MapGet("", async (Guid id, AgentWorkflowService workflows, CancellationToken token) =>
            await workflows.FindAsync(id, token) is { } row ? Results.Ok(AgentWorkflowService.View(row)) : Results.NotFound());
        group.MapGet("/analysis", async (Guid id, AgentWorkflowService workflows, CancellationToken token) =>
            await workflows.FindAsync(id, token) is { } row && AgentWorkflowService.ReadAnalysis(row) is { } analysis
                ? Results.Ok(analysis) : Results.NotFound());
        group.MapGet("/resume", async (Guid id, AgentWorkflowService workflows, CancellationToken token) =>
            await workflows.FindAsync(id, token) is { } row && AgentWorkflowService.ReadResume(row) is { } resume
                ? Results.Ok(new { resume, row.JobDescription }) : Results.NotFound());
    }

    private static async ValueTask<object?> HandleStorageError(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            return await next(context);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Agent workflow storage is unavailable.",
                detail: ex is SqlException { Number: 208 } or DbUpdateException { InnerException: SqlException { Number: 208 } }
                    ? "Apply the AddAgentWorkflows migration and retry."
                    : "Check the JobPilot database and retry this workflow.");
        }
    }
}
