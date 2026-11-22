
using System.Text.Json;
using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

public class CandidateProfileService(
    IWebHostEnvironment environment)
{
    public async Task<CandidateProfile> GetProfileAsync(
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(
            environment.ContentRootPath,
            "Data",
            "candidate-profile.json");

        await using var stream = File.OpenRead(path);

        var profile =
            await JsonSerializer.DeserializeAsync<CandidateProfile>(
                stream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                },
                cancellationToken);

        return profile ?? throw new InvalidOperationException(
            "Candidate profile is empty.");
    }
}
