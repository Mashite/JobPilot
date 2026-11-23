using System.Net.Http.Json;
using JobPilot.Web.Models;
using Microsoft.JSInterop;

namespace JobPilot.Web.Services;

internal sealed class ResumeWorkflow(IJSRuntime js)
{
    public async Task<ResumeSaveResult> SaveAsync(HttpClient client, int applicationId, GeneratedResume draft, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            $"api/applications/{applicationId}/resumes", draft, cancellationToken);
        await response.EnsureJobPilotSuccessAsync(cancellationToken);
        var version = await response.Content.ReadFromJsonAsync<SavedVersionResponse>(cancellationToken)
            ?? throw new InvalidOperationException("The API did not return the saved version.");
        return new ResumeSaveResult(version.Id, response.StatusCode == System.Net.HttpStatusCode.Created);
    }

    public async Task<List<ResumeVersion>> ListVersionsAsync(HttpClient client, int applicationId, CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<List<ResumeVersion>>($"api/applications/{applicationId}/resumes", cancellationToken) ?? [];

    public async Task DownloadAsync(HttpClient client, GeneratedResume draft, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("api/resumes/export-docx", draft, cancellationToken);
        await response.EnsureJobPilotSuccessAsync(cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await js.InvokeVoidAsync(
            "jobPilot.downloadFile",
            cancellationToken,
            "JobPilotResume.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Convert.ToBase64String(bytes));
    }
    private sealed record SavedVersionResponse(int Id);
}

internal sealed record ResumeVersion(int Id, DateTime CreatedAtUtc);
internal sealed record ResumeSaveResult(int Id, bool Created);
