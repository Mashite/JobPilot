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

public sealed class MasterCvTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("jobpilot-master-cv-");
    private readonly SkillNormalizer normalizer = new();

    [Fact]
    public async Task LoadsLegacyProfileAndPersistsEnrichedEntriesAndWorkOrderAcrossServiceInstances()
    {
        var profiles = await SeedProfileAsync();
        var loaded = await profiles.GetSnapshotAsync();
        var form = JsonSerializer.Deserialize<CandidateProfileForm>(
            JsonSerializer.Serialize(loaded.Profile, JsonSerializerOptions.Web),
            JsonSerializerOptions.Web)!;
        form.PrepareForEditing();
        Assert.Equal("Legacy degree", Assert.Single(form.EducationDetails!).Degree);
        Assert.Equal("Legacy project", Assert.Single(form.ProjectDetails!).Name);
        Assert.True(form.Experiences[0].IsCurrentPosition);

        form.Email = "candidate@example.invalid";
        form.EducationDetails![0].Institution = "Example University";
        form.ProjectDetails![0].Technologies.Add("C#");
        form.Certifications.Add(new() { Name = "Example Certification", Issuer = "Example Board" });
        form.Experiences[0].Achievements.Add("Improved build reliability.");
        form.Experiences.Reverse();
        form.TechnicalSkills = ["C#", "c sharp", "  SQL    Server ", "MSSQL"];
        var edited = JsonSerializer.Deserialize<CandidateProfile>(
            JsonSerializer.Serialize(form, JsonSerializerOptions.Web),
            JsonSerializerOptions.Web)!;
        Assert.Empty(new CandidateProfileValidator().ValidateForSave(edited));
        Assert.NotNull(await profiles.SaveProfileAsync(edited, loaded.Version));

        var restored = await CreateService().GetSnapshotAsync();
        Assert.NotEqual(loaded.Version, restored.Version);
        Assert.Equal("candidate@example.invalid", restored.Profile.Email);
        Assert.Equal(["C#", "SQL Server"], restored.Profile.TechnicalSkills);
        Assert.Equal(["Earlier Role", "Current Role"], restored.Profile.Experiences.Select(entry => entry.Position));
        Assert.Contains("Improved build reliability.", restored.Profile.Experiences[1].Achievements);
        Assert.Equal("keep", restored.Profile.Experiences[1].AdditionalFields!["reference"].GetString());
        Assert.True(restored.Profile.AdditionalFields!["metadata"].GetBoolean());
        Assert.Equal("Example Certification", Assert.Single(restored.Profile.Certifications).Name);
        Assert.Equal("Example University", Assert.Single(restored.Profile.EducationDetails!).Institution);
        Assert.Contains("Example University", Assert.Single(restored.Profile.Education));
        Assert.Contains("C#", Assert.Single(restored.Profile.Projects));
    }

    [Fact]
    public async Task StaleFullSaveCannotOverwriteSkillsAddedBySkillReview()
    {
        var profiles = await SeedProfileAsync();
        var opened = await profiles.GetSnapshotAsync();
        opened.Profile.Summary = "Unsaved edit";
        await CreateService().AddVerifiedSkillsAsync(["Docker"]);

        Assert.Null(await profiles.SaveProfileAsync(opened.Profile, opened.Version));
        var latest = await profiles.GetProfileAsync();
        Assert.Contains("Docker", latest.TechnicalSkills);
        Assert.Equal("Original summary", latest.Summary);
    }

    [Fact]
    public void ValidationRejectsInvalidContactLinksSkillsAndIncompleteEntries()
    {
        var profile = new CandidateProfile
        {
            Email = "not an email",
            LinkedIn = "file:///private",
            TechnicalSkills = ["!!!"],
            Experiences = [new() { Company = "Example", Position = "Dev" }],
            EducationDetails = [new()],
            Certifications = [new() { Name = "Example Certification", Url = "javascript:alert(1)" }]
        };

        var errors = new CandidateProfileValidator().ValidateForSave(profile);

        Assert.Contains("FullName", errors.Keys);
        Assert.Contains("ProfessionalTitle", errors.Keys);
        Assert.Contains("Email", errors.Keys);
        Assert.Contains("LinkedIn", errors.Keys);
        Assert.Contains("TechnicalSkills", errors.Keys);
        Assert.Contains("Experiences", errors.Keys);
        Assert.Contains("Education", errors.Keys);
        Assert.Contains("Certifications", errors.Keys);
    }

    [Fact]
    public async Task MatchingAndResumeGenerationUseTheLatestSavedProfile()
    {
        var profiles = await SeedProfileAsync();
        var opened = await profiles.GetSnapshotAsync();
        opened.Profile.TechnicalSkills.Add("Docker");
        opened.Profile.ProfessionalTitle = "Platform Developer";
        opened.Profile.Summary = "Maintains verified development tooling.";
        opened.Profile.Email = "private@example.invalid";
        opened.Profile.Experiences[0].Technologies.Add("C#");
        opened.Profile.Experiences[0].Achievements.Add("Reduced build failures.");
        await profiles.SaveProfileAsync(opened.Profile, opened.Version);
        var latest = await CreateService().GetProfileAsync();

        var matcher = new JobMatchingService(normalizer);
        var analysis = new JobAnalysis { Requirements = [new() { Skill = "Docker" }, new() { Skill = "Kubernetes" }] };
        var match = matcher.Match(analysis, latest);
        Assert.Equal(["Docker"], match.MatchingSkills);
        Assert.Equal(50, match.MatchScore);

        var handler = new DraftHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var resume = await new ResumeGeneratorService(new OllamaModelProvider(client, "test"), normalizer)
            .GenerateAsync("Use Docker.", latest);
        Assert.Equal(["Docker"], resume.TechnicalSkills);
        Assert.Equal("Platform Developer", resume.ProfessionalTitle);
        Assert.Equal("Maintains verified development tooling.", resume.ProfessionalSummary);
        Assert.Contains("Reduced build failures.", Assert.Single(resume.Experiences).Highlights);
        Assert.Contains("Technologies: C#", Assert.Single(resume.Experiences).Highlights);
        Assert.DoesNotContain("private@example.invalid", handler.RequestBody);
    }

    private async Task<CandidateProfileService> SeedProfileAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "candidate-profile.json"), """
            {
              "fullName": "Test Candidate",
              "professionalTitle": "Developer",
              "summary": "Original summary",
              "technicalSkills": ["C#"],
              "experiences": [
                {"company":"Example", "position":"Current Role", "startDate":"Jan 2022", "endDate":"Present",
                 "responsibilities":["Maintained builds."], "reference":"keep"},
                {"company":"Other Example", "position":"Earlier Role", "startDate":"2020", "endDate":"2021"}
              ],
              "education": ["Legacy degree"],
              "projects": ["Legacy project"],
              "metadata": true
            }
            """);
        return CreateService();
    }

    private CandidateProfileService CreateService()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory.FullName });
        var settings = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["CandidateProfile:Path"] = "candidate-profile.json" }).Build();
        return new CandidateProfileService(builder.Environment, settings, normalizer);
    }

    public void Dispose()
    {
        foreach (var file in directory.EnumerateFiles())
            file.Delete();

        directory.Delete();
    }

    private sealed class DraftHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    message = new
                    {
                        content = """{"selectedSkills":["Docker","Kubernetes"],"selectedExperienceIndices":[0]}"""
                    }
                })
            };
        }
    }
}
