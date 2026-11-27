using System.IO.Compression;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using JobPilot.Api.Models;
using JobPilot.Api.Services;

namespace JobPilot.Api.Endpoints;

internal static class MasterCvImportEndpoints
{
    public const int MaxDocumentBytes = 10 * 1024 * 1024;

    public static void MapMasterCvImport(this IEndpointRouteBuilder endpoints)
    {
        var imports = endpoints.MapGroup("/api/profile/import").AddEndpointFilter(ProfileEndpoints.RequireLocalProfileWriter);
        imports.MapPost("/preview", Preview);
        imports.MapPost("/confirm", Confirm);
    }

    private static async Task<IResult> Preview(HttpContext context, IWebHostEnvironment environment, MasterCvImporter importer,
        CandidateProfileService profiles, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.ContentLength is > MaxDocumentBytes)
                return Results.BadRequest("Select a DOCX of at most 10 MB.");

            await using var input = new MemoryStream();
            var source = context.Request.Body;
            var localPath = Path.Combine(environment.ContentRootPath, "Data", "Import", "MasterCV.docx");
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (input.Length + read > MaxDocumentBytes)
                    return Results.BadRequest("Select a DOCX of at most 10 MB.");
                await input.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (input.Length == 0)
            {
                var file = new FileInfo(localPath);
                if (!file.Exists || file.Length > MaxDocumentBytes)
                    return Results.BadRequest("Select a DOCX, or place a file of at most 10 MB at Data/Import/MasterCV.docx.");
                await using var local = file.OpenRead();
                await local.CopyToAsync(input, cancellationToken);
            }

            input.Position = 0;
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (zip.Entries.Count > 4000 || zip.Entries.Sum(entry => entry.Length) > 60L * 1024 * 1024)
                    return Results.BadRequest("The DOCX expands past the import size limit.");
            }
            input.Position = 0;
            var parsed = importer.Parse(input);
            var current = await profiles.GetSnapshotAsync(cancellationToken);
            context.Response.Headers.ETag = current.Version;
            context.Response.Headers.CacheControl = "no-store";
            var diagnostics = environment.IsDevelopment() ? new
            {
                mainSections = parsed.ImportReviewSections.Where(section => section.Kind is not (null or "review"))
                    .Select(section => section.Heading).ToArray(),
                employmentRecords = parsed.Experiences.Count,
                projectCount = parsed.ProjectDetails?.Count ?? 0,
                professionalProfileExtracted = !string.IsNullOrWhiteSpace(parsed.Summary),
                coreStrengthsCount = parsed.CoreStrengths.Count,
                technicalSkillsCount = CountTechnicalSkills(parsed),
                employerTechnologyCounts = parsed.Experiences.Select(role => role.Technologies.Count).ToArray(),
                unrecognizedSectionCount = parsed.ImportReviewSections.Count(section => section.Kind is null or "review"),
                sectionsNeedingReview = parsed.ImportReviewSections.Count(section => section.RequiresReview)
            } : null;
            return Results.Ok(new { profile = parsed, conflicts = MasterCvMerge.FindConflicts(current.Profile, parsed), diagnostics });
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or System.Xml.XmlException or ArgumentException)
        {
            return Results.BadRequest("The file could not be parsed as a Word DOCX.");
        }
        catch (Exception ex) when (ProfileEndpoints.IsStorageError(ex))
        {
            return ProfileEndpoints.StorageUnavailable();
        }
    }

    private static int CountTechnicalSkills(CandidateProfile profile)
    {
        var headings = profile.ImportReviewSections.Where(section => section.Kind == "skills")
            .Select(section => section.Heading).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return profile.CareerEvidence.Where(item => headings.Contains(item.SourceSection) || item.Category == "Used technologies")
            .Select(item => item.NormalizedName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    private static async Task<IResult> Confirm(ImportRequest request, HttpContext context, CandidateProfileService profiles,
        MasterCvMerge merger, CandidateProfileValidator validator, CancellationToken cancellationToken)
    {
        if (!request.Confirmed || request.Profile is not { } incoming)
            return Results.BadRequest("Review the preview and explicitly confirm the merge.");
        var version = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(version))
            return Results.StatusCode(StatusCodes.Status428PreconditionRequired);
        if (incoming.Experiences is not { Count: <= 100 } || incoming.Experiences.Any(entry => entry is null) ||
            incoming.Languages is null || incoming.CoreStrengths is null || incoming.Certifications is null ||
            incoming.Certifications.Any(entry => entry is null) ||
            incoming.EducationDetails?.Any(entry => entry is null) == true ||
            incoming.ProjectDetails?.Any(entry => entry is null) == true ||
            incoming.ImportReviewSections is not { Count: <= 1000 } || incoming.ImportReviewSections.Any(section =>
                section is null || section.Lines is null || section.Lines.Any(line => line is null)) ||
            incoming.CareerEvidence is not { Count: <= 3000 } || incoming.CareerEvidence.Any(item => item is null ||
                !CandidateProfileService.IsValidSkill(item.OriginalName) || string.IsNullOrWhiteSpace(item.SourceExcerpt) ||
                item.SourceExcerpt.Length > 4000 || string.IsNullOrWhiteSpace(item.SourceSection) ||
                string.IsNullOrWhiteSpace(item.Category) || !Enum.IsDefined(item.Classification) || !Enum.IsDefined(item.Verification)))
            return Results.BadRequest("The imported experiences or evidence could not be validated.");
        try
        {
            var result = await profiles.MergeImportAsync(version, current =>
            {
                var merged = merger.Merge(current, incoming, request.ReplaceProfessionalProfile);
                var errors = validator.ValidateForSave(merged);
                if (errors.Count > 0)
                    throw new InvalidImportException(errors);
                return merged;
            }, cancellationToken);
            return result is null ? Results.StatusCode(StatusCodes.Status412PreconditionFailed) :
                ProfileEndpoints.ProfileResponse(context, result);
        }
        catch (InvalidImportException ex)
        {
            return Results.ValidationProblem(ex.Errors);
        }
        catch (Exception ex) when (ProfileEndpoints.IsStorageError(ex))
        {
            return ProfileEndpoints.StorageUnavailable();
        }
    }

    private sealed record ImportRequest(bool Confirmed, CandidateProfile? Profile, bool ReplaceProfessionalProfile = false);
    private sealed class InvalidImportException(Dictionary<string, string[]> errors) : Exception
    {
        public Dictionary<string, string[]> Errors { get; } = errors;
    }
}
