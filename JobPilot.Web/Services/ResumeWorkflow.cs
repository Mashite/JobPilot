using System.Net.Http.Json;
using JobPilot.Web.Models;
using Microsoft.JSInterop;

namespace JobPilot.Web.Services;

internal sealed class ResumeWorkflow(IJSRuntime js)
{
    public async Task SaveAsync(HttpClient client, int applicationId, GeneratedResume draft, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            $"api/applications/{applicationId}/resumes", draft, cancellationToken);
        await response.EnsureJobPilotSuccessAsync(cancellationToken);
    }

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
}
