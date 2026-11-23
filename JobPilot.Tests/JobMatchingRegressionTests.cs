using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobPilot.Api.Endpoints;
using JobPilot.Api.Models;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Agent;
using JobPilot.Api.Services.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobPilot.Tests;

public sealed class JobMatchingRegressionTests : IDisposable
{
    private const string Listing = """
        Requirements:
        - 3+ years of proven experience in full stack development
        - Strong proficiency in C# .NET Framework and ASP.NET Core
        - Expertise in React, including state management (Context API)
        - Experience using AI tools to enhance productivity and quality
        - Database technologies: PostgreSQL, Entity Framework
        - Problem-solving and communication skills
        - Customer-focused problem solving

        Preferred:
        - AWS
        - DevOps practices and CI/CD pipelines
        - Software design patterns and principles
        - Bachelor's degree in computer science, software engineering, or related discipline
        - Slot-style games
        - Retail sale/rental kiosks
        - Augmented reality (AR)
        """;

    private static readonly string[] VerifiedSkills = ["C#", "ASP.NET Core", "Entity Framework Core", "Azure DevOps", "CI/CD"];
    private readonly DirectoryInfo storage = Directory.CreateTempSubdirectory("jobpilot-match-test-");
    private readonly SkillNormalizer normalizer = new();

    [Fact]
    public async Task ApiSplitsCompoundsAndMatchesOnlyPersistedEvidence()
    {
        var (app, logs) = await StartApiAsync(VerifiedSkills);
        await using var server = app;
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        var profile = await client.GetFromJsonAsync<CandidateProfile>("/api/profile");
        var result = await MatchAsync(client);

        Assert.Equal(VerifiedSkills, profile!.TechnicalSkills);
        Assert.Equal(5, result.VerifiedSkillCount);
        Assert.Equal(17, result.MatchScore);
        Assert.Equal("C#", Detail(result, "C#").ProfileSkill);
        Assert.Equal("exact", Detail(result, "C#").Status);
        Assert.Equal("exact", Detail(result, "ASP.NET Core").Status);
        Assert.Equal("exact", Detail(result, "CI/CD").Status);
        Assert.Equal("preferred", Detail(result, "CI/CD").Requirement.Importance);
        Assert.Equal("Strong proficiency in C# .NET Framework and ASP.NET Core", Detail(result, "C#").Requirement.OriginalText);
        Assert.Equal("related", Detail(result, "Entity Framework").Status);
        Assert.Equal("Entity Framework Core", Detail(result, "Entity Framework").ProfileSkill);
        Assert.Equal("related", Detail(result, "DevOps").Status);
        Assert.Equal(36, Detail(result, "Full stack development").Requirement.MinimumExperienceMonths);
        foreach (var skill in new[] { "React", "PostgreSQL", "AWS", ".NET Framework" })
            Assert.Equal("missing", Detail(result, skill).Status);

        var log = Assert.Single(logs.Messages);
        Assert.Contains("provider OpenAI model mock-model", log);
        Assert.Contains("extraction succeeded True", log);
        Assert.Contains("extracted requirements 14 normalized requirements 19", log);
        Assert.Contains("verified profile skills 5 normalized verified skills 5", log);
        Assert.Contains("exact 3 related 2 missing 14 score 17", log);
        Assert.DoesNotContain("Synthetic Candidate", log);
        Assert.DoesNotContain("Strong proficiency", log);

        var review = new
        {
            analysis = new { result.JobTitle, result.Requirements },
            confirmedSkills = new[] { "Entity Framework" },
            confirmed = true
        };
        using var preview = await client.PostAsJsonAsync("/api/jobs/skill-review/preview", review);
        var previewResult = await preview.Content.ReadFromJsonAsync<JobMatchResult>();
        Assert.Equal("exact", Detail(previewResult!, "Entity Framework").Status);
        Assert.Contains("pending profile save", Detail(previewResult!, "Entity Framework").Reason);
        Assert.Equal("related", Detail(await MatchAsync(client), "Entity Framework").Status);

        using var save = new HttpRequestMessage(HttpMethod.Post, "/api/profile/skills") { Content = JsonContent.Create(review) };
        save.Headers.Add("X-JobPilot-Profile-Write", "1");
        using var saved = await client.SendAsync(save);
        saved.EnsureSuccessStatusCode();

        var refreshed = await MatchAsync(client);
        Assert.Equal("exact", Detail(refreshed, "Entity Framework").Status);
        Assert.Equal("Entity Framework", Detail(refreshed, "Entity Framework").ProfileSkill);
        Assert.True(refreshed.MatchScore > result.MatchScore);
    }

    [Fact]
    public async Task EmptyPersistedMasterCvReturnsAClearWarning()
    {
        var (app, _) = await StartApiAsync([]);
        await using var server = app;
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        var result = await MatchAsync(client);

        Assert.Equal(0, result.MatchScore);
        Assert.Empty(result.MatchingSkills);
        Assert.Contains(result.Warnings, warning => warning.Contains("No verified technical skills"));
    }

    [Fact]
    public async Task ExtractionFailureIsLoggedWithoutProfileOrProviderMessage()
    {
        var (app, logs) = await StartApiAsync(VerifiedSkills, failExtraction: true);
        await using var server = app;
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var response = await client.PostAsJsonAsync("/api/jobs/match", new { jobDescription = Listing });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var log = Assert.Single(logs.Messages);
        Assert.Contains("stage extraction extraction succeeded False", log);
        Assert.Contains("verified profile skills 5", log);
        Assert.Contains("error type AiProviderException", log);
        Assert.DoesNotContain("mock provider detail", log);
        Assert.DoesNotContain("Synthetic Candidate", log);
    }

    [Fact]
    public void FamilyEvidenceAndUnverifiedDurationDoNotEarnExactCredit()
    {
        var analysis = new JobAnalysis
        {
            Requirements =
            [
                new() { Skill = ".NET Framework" },
                new() { Skill = "C#", MinimumExperienceMonths = 36 },
                new() { Skill = "React" },
                new() { Skill = "PostgreSQL" },
                new() { Skill = "AWS" }
            ]
        };
        var profile = new CandidateProfile { TechnicalSkills = [".NET 8", "C Sharp", "Blazor", "SQL Server", "Azure"] };
        var result = new JobMatchingService(normalizer).Match(analysis, profile);

        Assert.Equal(0, result.MatchScore);
        Assert.Equal(".NET 8", Detail(result, ".NET Framework").ProfileSkill);
        Assert.Equal("related", Detail(result, ".NET Framework").Status);
        Assert.Equal("related", Detail(result, "C#").Status);
        Assert.True(Detail(result, "C#").CanConfirmSkill);
        Assert.True(Detail(result, "C#").RequiresReviewEvidence);
        Assert.False(Detail(result, "C#").CanSaveToMasterProfile);
        Assert.All(new[] { "React", "PostgreSQL", "AWS" }, skill => Assert.Equal("missing", Detail(result, skill).Status));
    }

    [Theory]
    [InlineData("technology", "AWS", true)]
    [InlineData("soft_skill", "Customer problem solving", true)]
    [InlineData("domain", "Retail kiosks", true)]
    [InlineData("education", "Bachelor's degree", false)]
    [InlineData("experience", "Full stack development", false)]
    [InlineData("other", "Software design patterns", false)]
    public async Task EveryCategoryCanBeReviewedButOnlySkillsCanBeSaved(string category, string skill, bool savable)
    {
        var (app, _) = await StartApiAsync([]);
        await using var server = app;
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var analysis = new JobAnalysis
        {
            Requirements = [new() { Skill = skill, Category = category, MinimumExperienceMonths = category == "experience" ? 36 : null }]
        };
        var initial = new JobMatchingService(normalizer).Match(analysis, new CandidateProfile());
        var detail = Assert.Single(initial.RequirementMatches);
        Assert.True(detail.CanConfirmSkill);
        Assert.Equal(savable, detail.CanSaveToMasterProfile);
        Assert.Equal(!savable, detail.RequiresReviewEvidence);
        Assert.Equal([skill], initial.MissingOrUnverifiedSkills);

        if (!savable)
        {
            using var missing = await client.PostAsJsonAsync("/api/jobs/skill-review/preview", new
            {
                analysis, confirmedSkills = new[] { skill }, confirmed = true
            });
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        }

        const string note = "See the relevant Master CV entry for the dates and supporting details.";
        var review = new
        {
            analysis, confirmedSkills = new[] { skill }, confirmed = true,
            confirmedEvidence = new Dictionary<string, string> { [skill] = note }
        };
        using var preview = await client.PostAsJsonAsync("/api/jobs/skill-review/preview", review);
        preview.EnsureSuccessStatusCode();
        var reviewed = (await preview.Content.ReadFromJsonAsync<JobMatchResult>())!;
        Assert.Equal(100, reviewed.MatchScore);
        Assert.Equal([skill], reviewed.MatchingSkills);
        Assert.Empty(reviewed.MissingOrUnverifiedSkills);
        Assert.Equal(note, Assert.Single(reviewed.RequirementMatches).ReviewEvidence);
        Assert.Empty((await client.GetFromJsonAsync<CandidateProfile>("/api/profile"))!.TechnicalSkills);

        using var save = new HttpRequestMessage(HttpMethod.Post, "/api/profile/skills") { Content = JsonContent.Create(review) };
        save.Headers.Add("X-JobPilot-Profile-Write", "1");
        using var saved = await client.SendAsync(save);
        Assert.Equal(savable ? HttpStatusCode.OK : HttpStatusCode.BadRequest, saved.StatusCode);
        var profile = (await client.GetFromJsonAsync<CandidateProfile>("/api/profile"))!;
        Assert.Equal(savable ? new[] { skill } : [], profile.TechnicalSkills);
        Assert.Empty(profile.Education);
        Assert.Single(profile.Experiences);
    }

    [Fact]
    public void EmptyExtractionIsDistinguishedFromAProfileThatDoesNotMatch()
    {
        var result = new JobMatchingService(normalizer).Match(new JobAnalysis(), new CandidateProfile { TechnicalSkills = ["C#"] });

        Assert.Contains("No requirements were extracted", Assert.Single(result.Warnings));
    }

    private RequirementMatch Detail(JobMatchResult result, string skill) =>
        Assert.Single(result.RequirementMatches, item => normalizer.Normalize(item.Requirement.Skill) == normalizer.Normalize(skill));

    private static async Task<JobMatchResult> MatchAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/jobs/match", new { jobDescription = Listing });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobMatchResult>())!;
    }

    private async Task<(WebApplication App, MatchLog Logs)> StartApiAsync(string[] skills, bool failExtraction = false)
    {
        var file = Path.Combine(storage.FullName, "candidate-profile.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new CandidateProfile
        {
            FullName = "Synthetic Candidate",
            TechnicalSkills = skills.ToList(),
            Experiences = [new() { Responsibilities = ["Used C# and ASP.NET Core."] }]
        }, JsonSerializerOptions.Web));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = storage.FullName });
        builder.Configuration["CandidateProfile:Path"] = file;
        var logs = new MatchLog();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(normalizer);
        builder.Services.AddSingleton(new AiProviderSelection("OpenAI", "mock-model"));
        builder.Services.AddSingleton<IAiModelProvider>(new MockExtraction(failExtraction));
        builder.Services.AddSingleton<CandidateProfileService>();
        builder.Services.AddScoped<JobAnalysisService>();
        builder.Services.AddScoped<JobMatchingService>();
        builder.Services.AddScoped<CandidateProfileValidator>();
        builder.Services.AddScoped<WordDocumentService>();
        builder.Services.AddScoped<ResumeGeneratorService>();
        builder.Services.AddScoped<JobPilotAgentService>();
        builder.Services.AddHttpClient();
        var app = builder.Build();
        app.MapJobPilotEndpoints();
        app.MapProfileEndpoints();
        app.MapSkillReviewEndpoints();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        return (app, logs);
    }

    public void Dispose()
    {
        foreach (var file in storage.EnumerateFiles())
            file.Delete();
        storage.Delete();
    }

    private sealed class MockExtraction(bool fail) : IAiModelProvider
    {
        public Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            if (fail)
                throw new AiProviderException("mock provider detail");
            Assert.Equal(Listing, Assert.IsType<AiUserMessage>(Assert.Single(request.History)).Text);
            var schema = request.OutputSchema!.Value.GetProperty("properties").GetProperty("requirements").GetProperty("items");
            Assert.Contains("minimumExperienceMonths", schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
            var output = new JobAnalysis
            {
                JobTitle = "Full stack developer",
                Requirements =
                [
                    Requirement("Full stack development", "3+ years of proven experience in full stack development", category: "experience", months: 36),
                    Requirement("Strong proficiency in C# .NET Framework and ASP.NET Core"),
                    Requirement("Expertise in React, including state management (Context API)"),
                    Requirement("AI tools", "Experience using AI tools to enhance productivity and quality"),
                    Requirement("Database technologies: PostgreSQL, Entity Framework"),
                    Requirement("Problem-solving and communication", "Problem-solving and communication skills", category: "soft_skill"),
                    Requirement("Customer-focused problem solving", category: "soft_skill"),
                    Requirement("AWS", importance: "preferred"),
                    Requirement("DevOps practices and CI/CD pipelines", importance: "preferred"),
                    Requirement("Software design patterns and principles", importance: "preferred"),
                    Requirement("Bachelor's degree", "Bachelor's degree in computer science, software engineering, or related discipline", "preferred", "education"),
                    Requirement("Slot-style games", importance: "preferred", category: "domain"),
                    Requirement("Retail sale/rental kiosks", importance: "preferred", category: "domain"),
                    Requirement("Augmented reality (AR)", importance: "preferred", category: "domain")
                ]
            };
            return Task.FromResult(new AiAssistantMessage(JsonSerializer.Serialize(output, JsonSerializerOptions.Web), []));
        }

        private static JobRequirement Requirement(string skill, string? original = null, string importance = "required",
            string category = "technology", int? months = null) => new()
        {
            Skill = skill, OriginalText = original ?? skill, Importance = importance, Category = category, MinimumExperienceMonths = months
        };
    }

    private sealed class MatchLog : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => categoryName == typeof(JobMatchingService).FullName ? this : NullLogger.Instance;
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
        public void Dispose() { }
    }
}
