using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobPilot.Api.Models;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Ai;
using JobPilot.Web.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace JobPilot.Tests;

public sealed class SkillReviewTests : IDisposable
{
    private readonly DirectoryInfo storage = Directory.CreateTempSubdirectory("jobpilot-profile-test-");
    private readonly SkillNormalizer normalizer = new();

    [Fact]
    public void MoveAndReturnKeepStagingSeparateFromConfirmation()
    {
        string[] original = ["SQL Server", "Docker"];
        var review = new SkillReviewState(original);

        Assert.False(review.Stage("Other skill"));
        Assert.True(review.Stage("Docker"));
        Assert.False(review.Stage("Docker"));
        Assert.Equal(["Docker"], review.Staged);
        Assert.Equal(["SQL Server"], review.Unmatched);
        Assert.Empty(review.Confirmed);

        Assert.False(review.Return("Docker"));
        Assert.Equal(original, review.Unmatched);
        review.Stage("Docker");
        review.Confirm();
        Assert.Empty(review.Staged);
        Assert.Equal(["Docker"], review.Confirmed);
        Assert.True(review.Return("Docker"));
        Assert.Equal(original, review.Unmatched);
        Assert.Empty(review.Confirmed);
    }

    [Fact]
    public void SavingAProfileSkillKeepsTheQualificationAndItsEvidenceInTheReview()
    {
        var review = new SkillReviewState(["AWS", "Software design patterns"]);
        review.Stage("AWS");
        review.Stage("Software design patterns");
        review.SetEvidence("Software design patterns", "  See the architecture project in Master CV.  ");
        review.Confirm();

        review.AcceptSavedMatches(["AWS"]);

        Assert.Equal(["Software design patterns"], review.Confirmed);
        Assert.Equal("See the architecture project in Master CV.",
            review.EvidenceFor(review.Confirmed)["Software design patterns"]);
        Assert.True(review.Return("Software design patterns"));
        Assert.Null(review.EvidenceFor("Software design patterns"));
    }

    [Fact]
    public async Task ConfirmedSkillsRecalculateWeightedScoreBeforeAndAfterExplicitSave()
    {
        var (profiles, _) = await CreateProfileAsync();
        var analysis = new JobAnalysis
        {
            Requirements =
            [
                new() { Skill = "C#", Importance = "required" },
                new() { Skill = "SQL Server", Importance = "required" },
                new() { Skill = "MSSQL", Importance = "preferred" },
                new() { Skill = "Docker", Importance = "preferred" }
            ]
        };
        var matcher = new JobMatchingService(normalizer);
        var initial = matcher.Match(analysis, await profiles.GetProfileAsync());
        var preview = matcher.Match(analysis, await profiles.GetProfileAsync(), ["MSSQL"]);

        Assert.Equal(43, initial.MatchScore);
        Assert.Equal(86, preview.MatchScore);
        Assert.Contains(preview.Evidence, evidence => evidence.Contains("pending profile save"));
        Assert.Equal(43, matcher.Match(analysis, await profiles.GetProfileAsync()).MatchScore);

        await profiles.AddVerifiedSkillsAsync(["MSSQL"]);
        var saved = matcher.Match(analysis, await profiles.GetProfileAsync());
        Assert.Equal(86, saved.MatchScore);
        Assert.DoesNotContain(saved.Evidence, evidence => evidence.Contains("pending profile save"));
        Assert.Equal(["C#"], initial.MatchingSkills);
        Assert.Equal(["SQL Server", "Docker"], initial.MissingOrUnverifiedSkills);
    }

    [Fact]
    public async Task ConcurrentWritesDeduplicateAliasesAndPreserveUnrelatedJson()
    {
        var (profiles, path) = await CreateProfileAsync();
        using var before = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        await Task.WhenAll(
            profiles.AddVerifiedSkillsAsync(["  SQL   Server ", "MSSQL", "c sharp"]),
            profiles.AddVerifiedSkillsAsync(["Docker"]));
        await profiles.AddVerifiedSkillsAsync([" Microsoft SQL Server "]);
        var reloaded = await profiles.GetProfileAsync();

        Assert.Equal(3, reloaded.TechnicalSkills.Count);
        Assert.Contains("C#", reloaded.TechnicalSkills);
        Assert.Contains("SQL Server", reloaded.TechnicalSkills);
        Assert.Contains("Docker", reloaded.TechnicalSkills);
        using var after = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        foreach (var property in before.RootElement.EnumerateObject().Where(property => property.Name != "technicalSkills"))
            Assert.True(JsonElement.DeepEquals(property.Value, after.RootElement.GetProperty(property.Name)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\nDocker")]
    [InlineData("???")]
    public async Task InvalidSkillsDoNotChangeTheProfile(string skill)
    {
        var (profiles, path) = await CreateProfileAsync();
        var before = await File.ReadAllTextAsync(path);

        await Assert.ThrowsAsync<ArgumentException>(() => profiles.AddVerifiedSkillsAsync([skill]));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ResumeUsesOnlySavedSkillsAndPreservesVerifiedEmploymentFacts()
    {
        var (profiles, _) = await CreateProfileAsync();
        using var client = new HttpClient(new DraftResponse()) { BaseAddress = new Uri("http://localhost/") };
        var generator = new ResumeGeneratorService(new OllamaModelProvider(client, "test"), normalizer);
        const string job = "Use C#, Docker, and Kubernetes.";

        var before = await generator.GenerateAsync(job, await profiles.GetProfileAsync());
        Assert.Equal(["C#"], before.TechnicalSkills);

        await profiles.AddVerifiedSkillsAsync(["Docker"]);
        var after = await generator.GenerateAsync(job, await profiles.GetProfileAsync());

        Assert.Equal(["Docker", "C#"], after.TechnicalSkills);
        Assert.Equal("Developer", after.ProfessionalTitle);
        Assert.Equal("Builds internal tools.", after.ProfessionalSummary);
        var experience = Assert.Single(after.Experiences);
        Assert.Equal("Example Company", experience.Company);
        Assert.Equal(["Maintained internal tools."], experience.Highlights);
    }

    private async Task<(CandidateProfileService Service, string Path)> CreateProfileAsync()
    {
        var path = Path.Combine(storage.FullName, "candidate-profile.json");
        await File.WriteAllTextAsync(path, """
            {
              "fullName": "Test Candidate",
              "professionalTitle": "Developer",
              "summary": "Builds internal tools.",
              "technicalSkills": ["C#"],
              "experiences": [{
                "company": "Example Company",
                "position": "Developer",
                "startDate": "2021",
                "endDate": "Present",
                "responsibilities": ["Maintained internal tools."],
                "referenceCode": "leave intact"
              }],
              "education": ["Example degree"],
              "projects": ["Example project"],
              "additionalMetadata": { "preferredRegion": "example" }
            }
            """);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = storage.FullName });
        var settings = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["CandidateProfile:Path"] = path }).Build();
        return (new CandidateProfileService(builder.Environment, settings, normalizer), path);
    }

    public void Dispose()
    {
        foreach (var file in storage.EnumerateFiles())
            file.Delete();

        storage.Delete();
    }

    private sealed class DraftResponse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    message = new
                    {
                        content = """
                            {
                              "professionalTitle": "Unverified title",
                              "professionalSummary": "Used Kubernetes at a previous employer.",
                              "selectedSkills": ["Docker", "Kubernetes", "c sharp", "C#"],
                              "selectedExperienceIndices": [0, 0, 99]
                            }
                            """
                    }
                })
            });
    }
}
