using System.Net;
using System.Text.Json;
using JobPilot.Api.Models;
using JobPilot.Api.Services;

namespace JobPilot.Api.Endpoints;

internal static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/profile", GetProfile);
        endpoints.MapPut("/api/profile", SaveProfile).AddEndpointFilter(RequireLocalProfileWriter);
    }

    public static ValueTask<object?> RequireLocalProfileWriter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var remote = http.Connection.RemoteIpAddress;
        if (remote?.IsIPv4MappedToIPv6 == true)
            remote = remote.MapToIPv4();

        if (remote is null || !IPAddress.IsLoopback(remote) ||
            http.Request.Headers.ContainsKey("Origin") ||
            http.Request.Headers.ContainsKey("Sec-Fetch-Site") ||
            http.Request.Headers["X-JobPilot-Profile-Write"] != "1")
        {
            return ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return next(context);
    }

    private static async Task<IResult> GetProfile(HttpContext http, CandidateProfileService profiles, CancellationToken cancellationToken)
    {
        try
        {
            return ProfileResponse(http, await profiles.GetSnapshotAsync(cancellationToken));
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return StorageUnavailable();
        }
    }

    private static async Task<IResult> SaveProfile(CandidateProfile profile, HttpContext http,
        CandidateProfileService profiles, CandidateProfileValidator validator, CancellationToken cancellationToken)
    {
        var expectedVersion = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(expectedVersion))
            return Results.StatusCode(StatusCodes.Status428PreconditionRequired);

        var errors = validator.ValidateForSave(profile);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        try
        {
            var saved = await profiles.SaveProfileAsync(profile, expectedVersion, cancellationToken);
            return saved is null ? Results.StatusCode(StatusCodes.Status412PreconditionFailed) : ProfileResponse(http, saved);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            return StorageUnavailable();
        }
    }

    internal static IResult ProfileResponse(HttpContext http, CandidateProfileSnapshot snapshot)
    {
        http.Response.Headers.ETag = snapshot.Version;
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(snapshot.Profile);
    }

    internal static bool IsStorageError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or
            System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException;

    internal static IResult StorageUnavailable() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable, title: "Master CV storage is unavailable.");
}
