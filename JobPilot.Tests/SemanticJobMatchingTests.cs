using System.Net.Http.Json;
using System.Text.Json;
using JobPilot.Api.Data;
using JobPilot.Api.Data.Entities;
using JobPilot.Api.Endpoints;
using JobPilot.Api.Models;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Ai;
using JobPilot.Api.Services.Agent;
using JobPilot.Web.Components.Pages;
using JobPilot.Web.Models;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JobPilot.Tests;

public sealed class SemanticJobMatchingTests
{
    private static readonly Guid ProgrammingEvidenceId = Guid.Parse("16f54e25-53d3-4f9d-a321-af669a40dca4");
    private readonly SkillNormalizer normalizer = new();

    [Fact]
    public async Task BatchesBroadCapabilitiesAndKeepsNamedTechnologiesGrounded()
    {
        using var cache = new SemanticMatchCache();
        var provider = new MockProvider();
        var matcher = Create(provider, cache);
        var analysis = Analysis(
            ("C#", "technology"),
            ("Advanced programming languages", "technology"),
            ("Manufacturing software development", "experience"),
            ("Customer problem solving", "soft_skill"),
            ("Software design principles", "other"),
            ("Software design patterns", "other"),
            ("React", "technology"), ("AWS", "technology"), ("Visual Basic", "experience"));
        var profile = Profile();

        var result = await matcher.MatchAsync(analysis, new(profile, "version-1"), default);

        Assert.Equal("completed", result.SemanticStatus);
        Assert.Equal(new[] { "exact", "semantic", "semantic", "semantic", "semantic", "partial", "missing", "missing", "missing" },
            result.RequirementMatches.Select(item => item.Status));
        Assert.Equal(61, result.MatchScore);
        Assert.Contains(result.RequirementMatches[1].Citations!, citation => citation.Id == ProgrammingEvidenceId.ToString("D"));
        var project = Assert.Single(result.RequirementMatches[2].Citations!);
        Assert.Equal("Plant console", project.Project);
        Assert.Equal("Synthetic Systems", project.Employer);
        Assert.Contains("PLC", project.Technologies!);
        Assert.Equal("responsibility", Assert.Single(result.RequirementMatches[3].Citations!).Kind);
        Assert.DoesNotContain("SOLID", result.RequirementMatches[4].Reason);
        Assert.DoesNotContain(profile.CareerEvidence, row => row.OriginalName == "Manufacturing software development");
        var prompt = Assert.Single(provider.Prompts);
        Assert.DoesNotContain("private@example.test", prompt);
        Assert.DoesNotContain("Synthetic Person", prompt);
        Assert.DoesNotContain("\"skill\":\"React\"", prompt);
        Assert.DoesNotContain("\"skill\":\"Visual Basic\"", prompt);

        var json = JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
        var display = JsonSerializer.Deserialize<JobAnalyzer.JobMatchResult>(json, JsonSerializerOptions.Web)!;
        Assert.Equal("AI Semantic Match", JobMatchPresentation.Label(display.RequirementMatches[2].Status));
        Assert.Equal("Plant console", Assert.Single(display.RequirementMatches[2].Citations!).Project);
        Assert.Equal("Partial Match", JobMatchPresentation.Label(display.RequirementMatches[5].Status));
        Assert.Equal("Needs Verification", JobMatchPresentation.Label("needs_verification"));
    }

    [Fact]
    public async Task ExactRoleTechnologyAndUnsupportedNamesAvoidProvider()
    {
        using var cache = new SemanticMatchCache();
        var provider = new MockProvider();
        var result = await Create(provider, cache).MatchAsync(Analysis(("PLC", "technology"), ("React", "technology")),
            new(Profile(), "version-1"), default);
        Assert.Equal("not_needed", result.SemanticStatus);
        Assert.Equal("exact", result.RequirementMatches[0].Status);
        Assert.Equal("Synthetic Systems", Assert.Single(result.RequirementMatches[0].Citations!).Employer);
        Assert.Equal("missing", result.RequirementMatches[1].Status);
        Assert.Empty(provider.Prompts);
    }

    [Theory]
    [InlineData("fake_evidence", "incomplete")]
    [InlineData("malformed", "unavailable")]
    [InlineData("provider_failure", "unavailable")]
    public async Task InvalidOrUnavailableSemanticResultsPreserveExactMatches(string failure, string status)
    {
        using var cache = new SemanticMatchCache();
        var provider = new MockProvider { Failure = failure };
        var matcher = Create(provider, cache);
        var analysis = Analysis(("C#", "technology"), ("Manufacturing software development", "experience"));
        var snapshot = new CandidateProfileSnapshot(Profile(), "version-1");

        var result = await matcher.MatchAsync(analysis, snapshot, default);
        Assert.Equal(status, result.SemanticStatus);
        Assert.Equal(new[] { "exact", "pending_semantic" }, result.RequirementMatches.Select(item => item.Status));
        Assert.True(result.IsScoreProvisional);
        Assert.Equal(50, result.MatchScore);
        Assert.Contains(result.Warnings, warning => warning.Contains("retry", StringComparison.OrdinalIgnoreCase));

        provider.Failure = null;
        var retried = await matcher.MatchAsync(analysis, snapshot, default);
        Assert.Equal("completed", retried.SemanticStatus);
        Assert.Equal(100, retried.MatchScore);
    }

    [Fact]
    public async Task CacheTracksRequirementsProfileAndModelAndManualPreviewDoesNotCallProvider()
    {
        using var cache = new SemanticMatchCache();
        var provider = new MockProvider();
        var matcher = Create(provider, cache);
        var job = Analysis(("Manufacturing software development", "experience"));
        var snapshot = new CandidateProfileSnapshot(Profile(), "version-1");
        await matcher.MatchAsync(job, snapshot, default);
        var cached = await matcher.MatchAsync(job, snapshot, default, allowProvider: false);
        Assert.Equal("semantic", Assert.Single(cached.RequirementMatches).Status);
        Assert.Single(provider.Prompts);
        await matcher.MatchAsync(job, snapshot with { Version = "version-2" }, default);
        await Create(provider, cache, model: "new-model").MatchAsync(job, snapshot, default);
        job.Requirements[0].OriginalText = "Develop software used on a factory floor.";
        await matcher.MatchAsync(job, snapshot, default);
        Assert.Equal(4, provider.Prompts.Count);
        var local = await Create(provider, cache, model: "local-model", name: "Ollama").MatchAsync(job, snapshot, default);
        Assert.Null(local.SemanticStatus);
        Assert.Equal(4, provider.Prompts.Count);
    }

    [Fact]
    public async Task JobReviewRestoresAcrossContextsAndChangesScoreWithoutChangingProfile()
    {
        using var cache = new SemanticMatchCache();
        var profile = Profile();
        var matcher = Create(new MockProvider(), cache);
        var analysis = Analysis(("C#", "technology"), ("Manufacturing software development", "experience"),
            ("Customer problem solving", "soft_skill"));
        analysis.Requirements[2].Importance = "preferred";
        var listing = "Manufacturing Engineer synthetic listing";
        var automatic = await matcher.MatchAsync(analysis, new(profile, "version-1"), default);
        var review = new JobReview([], ["Customer problem solving"], ["Manufacturing software development"],
            new() { ["Customer problem solving"] = "Documented troubleshooting. Review scope." });
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync();
        var options = new DbContextOptionsBuilder<JobPilotDbContext>().UseSqlite(sqlite).Options;
        await using (var writer = new TestContext(options))
        {
            await writer.Database.EnsureCreatedAsync();
            await new JobMatchReviewStore(writer, normalizer).SaveAsync(listing, automatic.Requirements, review, default);
        }
        await using var reader = new TestContext(options);
        var store = new JobMatchReviewStore(reader, normalizer);
        var restored = await store.LoadAsync(listing, automatic.Requirements, default);

        var result = matcher.ApplyReview(automatic, profile, restored!);
        Assert.Equal(new[] { "exact", "missing", "partial" }, result.RequirementMatches.Select(item => item.Status));
        Assert.Equal(50, result.MatchScore);
        Assert.Equal(100, automatic.MatchScore);
        Assert.Equal("Documented troubleshooting. Review scope.", result.RequirementMatches[2].ReviewEvidence);
        Assert.Single(profile.CareerEvidence);
        Assert.Null(await store.LoadAsync("Different listing", automatic.Requirements, default));
        await store.SaveAsync(listing, automatic.Requirements, new([], [], [], []), default);
        Assert.Null(await store.LoadAsync(listing, automatic.Requirements, default));
    }

    [Fact]
    public async Task ModelCannotVerifyRequiredDurationFromRoleDatesAlone()
    {
        using var cache = new SemanticMatchCache();
        var job = Analysis(("Manufacturing software development", "experience"));
        job.Requirements[0].MinimumExperienceMonths = 120;
        var result = await Create(new MockProvider(), cache).MatchAsync(job, new(Profile(), "version-1"), default);
        Assert.Equal("needs_verification", Assert.Single(result.RequirementMatches).Status);
        Assert.Equal(0, result.MatchScore);
    }

    [Fact]
    public async Task MatchEndpointRestoresSavedReviewAndKeepsAutomaticResults()
    {
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync();
        var options = new DbContextOptionsBuilder<JobPilotDbContext>().UseSqlite(sqlite).Options;
        var mock = new MockProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(normalizer);
        builder.Services.AddSingleton<IAiModelProvider>(mock);
        builder.Services.AddSingleton(new AiProviderSelection("OpenAI", "mock"));
        builder.Services.AddSingleton<JobRequirementCache>();
        builder.Services.AddSingleton<SemanticMatchCache>();
        builder.Services.AddScoped<JobMatchingService>();
        builder.Services.AddScoped<JobAnalysisService>();
        builder.Services.AddScoped<SemanticJobMatcher>();
        builder.Services.AddScoped<JobMatchReviewStore>();
        builder.Services.AddScoped<JobPilotDbContext>(_ => new TestContext(options));
        builder.Services.AddScoped(services => new CandidateProfileService(builder.Environment, builder.Configuration,
            normalizer, services.GetRequiredService<JobPilotDbContext>()));
        builder.Services.AddScoped<CandidateProfileValidator>();
        builder.Services.AddScoped<ResumeGeneratorService>();
        builder.Services.AddScoped<WordDocumentService>();
        builder.Services.AddScoped<JobPilotAgentService>();
        builder.Services.AddHttpClient();
        await using var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JobPilotDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.CareerProfiles.Add(new() { Id = 1, Version = "v1", ProfileJson = JsonSerializer.Serialize(Profile(), JsonSerializerOptions.Web) });
            await db.SaveChangesAsync();
        }
        app.MapJobPilotEndpoints();
        app.MapSkillReviewEndpoints();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-JobPilot-Profile-Write", "1");
        const string listing = "Manufacturing developer. Use C# and develop manufacturing software.";
        using var first = await client.PostAsJsonAsync("/api/jobs/match", new { jobDescription = listing });
        first.EnsureSuccessStatusCode();
        var match = (await first.Content.ReadFromJsonAsync<JobMatchResult>())!;
        Assert.Equal(100, match.MatchScore);

        using var saved = await client.PostAsJsonAsync("/api/jobs/skill-review/save", new
        {
            analysis = new { match.JobTitle, match.Requirements }, jobDescription = listing,
            confirmed = true, missingSkills = new[] { "Manufacturing software development" }
        });
        saved.EnsureSuccessStatusCode();
        Assert.Equal(50, (await saved.Content.ReadFromJsonAsync<JobMatchResult>())!.MatchScore);

        using var reopened = await client.PostAsJsonAsync("/api/jobs/match", new { jobDescription = listing });
        reopened.EnsureSuccessStatusCode();
        var stored = (await reopened.Content.ReadFromJsonAsync<JobMatchResult>())!;
        Assert.Equal(50, stored.MatchScore);
        Assert.Equal(100, stored.AutomaticResult!.MatchScore);
        Assert.Equal("Manufacturing software development", Assert.Single(stored.SavedReview!.Missing));
        Assert.Single(mock.Prompts);
        Assert.Equal(1, mock.ExtractionRequests);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JobPilotDbContext>();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE JobMatchReviews RENAME TO UnavailableJobMatchReviews");
        }
        using var missingReviewTable = await client.PostAsJsonAsync("/api/jobs/match", new { jobDescription = listing });
        missingReviewTable.EnsureSuccessStatusCode();
        var fallback = (await missingReviewTable.Content.ReadFromJsonAsync<JobMatchResult>())!;
        Assert.Equal(100, fallback.MatchScore);
        Assert.Equal("semantic", fallback.RequirementMatches[1].Status);
        Assert.Contains(fallback.Warnings, warning => warning.Contains("saved job review"));
        Assert.Null(fallback.SavedReview);
        Assert.Single(mock.Prompts);

        using var unavailableSave = await client.PostAsJsonAsync("/api/jobs/skill-review/save", new
        {
            analysis = new { match.JobTitle, match.Requirements }, jobDescription = listing,
            confirmed = true, missingSkills = new[] { "Manufacturing software development" }
        });
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, unavailableSave.StatusCode);
        Assert.Contains("Job review storage is unavailable.", await unavailableSave.Content.ReadAsStringAsync());
    }

    private SemanticJobMatcher Create(MockProvider provider, SemanticMatchCache cache, string model = "mock", string name = "OpenAI") =>
        new(new JobMatchingService(normalizer), normalizer, provider, new(name, model), cache,
            NullLogger<SemanticJobMatcher>.Instance);

    private static JobAnalysis Analysis(params (string Skill, string Category)[] requirements) => new()
    {
        Requirements = requirements.Select(item => new JobRequirement
        {
            Skill = item.Skill, Category = item.Category, Importance = "required", OriginalText = item.Skill
        }).ToList()
    };

    private static CandidateProfile Profile() => new()
    {
        FullName = "Synthetic Person", Email = "private@example.test",
        Summary = "Synthetic Person works on software. Contact private@example.test.",
        ProfessionalTitle = "Senior Software Engineer",
        TechnicalSkills = ["C#", "Java", ".NET", "Blazor", "JavaScript", "ASP.NET MVC", "Azure"],
        CareerEvidence = [new(ProgrammingEvidenceId, "Enterprise software development", "enterprise software development",
            "Professional competency", EvidenceClassification.ExplicitlyDocumented, EvidenceVerification.ImportConfirmed,
            "Synthetic Systems", null, "Developed enterprise software in advanced programming languages C# and Java.",
            "Experience", DateTimeOffset.UnixEpoch)],
        Experiences = [new()
        {
            Company = "Synthetic Systems", Position = "Senior Software Engineer", StartDate = "2020", EndDate = "Present",
            Technologies = ["PLC", "C#", "Java"],
            Responsibilities = [
                "Solved customer problems through requirements analysis, meetings, troubleshooting and implementation.",
                "Applied software design principles in a reusable framework with separation of shared and customer-specific logic."],
            Achievements = ["Built manufacturing production monitoring software with PLC integration."]
        }],
        ProjectDetails = [new()
        {
            Employer = "Synthetic Systems", Name = "Plant console", Technologies = ["PLC"],
            Description = "Developed manufacturing software for an MES and Andon platform spanning multiple factories."
        }]
    };

    private sealed class MockProvider : IAiModelProvider
    {
        public List<string> Prompts { get; } = [];
        public int ExtractionRequests { get; private set; }
        public string? Failure { get; set; }

        public Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Assert.NotNull(request.OutputSchema);
            if (request.OutputSchema!.Value.GetProperty("properties").TryGetProperty("requirements", out _))
            {
                ExtractionRequests++;
                return Task.FromResult(new AiAssistantMessage("""
                    {"jobTitle":"Manufacturing developer","requirements":[
                    {"skill":"C#","category":"technology","importance":"required"},
                    {"skill":"Manufacturing software development","category":"experience","importance":"required"}]}
                    """, []));
            }
            var prompt = Assert.IsType<AiUserMessage>(Assert.Single(request.History)).Text;
            Prompts.Add(prompt);
            if (Failure == "provider_failure")
                throw new AiProviderException("Unavailable", 503);
            if (Failure == "malformed")
                return Task.FromResult(new AiAssistantMessage("unstructured output", []));
            using var json = JsonDocument.Parse(prompt);
            var root = json.RootElement;
            var evidence = root.GetProperty("evidence").EnumerateArray().ToDictionary(
                row => row.GetProperty("Id").GetString()!, row => row.GetProperty("Excerpt").GetString()!);
            var results = root.GetProperty("requirements").EnumerateArray().Select(item =>
            {
                var skill = item.GetProperty("requirement").GetProperty("skill").GetString();
                var allowed = item.GetProperty("allowedEvidenceIds").EnumerateArray().Select(value => value.GetString()!);
                var anchor = skill switch
                {
                    "Advanced programming languages" => "Developed enterprise software",
                    "Manufacturing software development" => "MES and Andon",
                    "Customer problem solving" => "Solved customer",
                    _ => "reusable framework"
                };
                return new
                {
                    requirementId = item.GetProperty("requirementId").GetInt32(),
                    classification = "SemanticMatch",
                    reason = skill == "Software design principles"
                        ? "The candidate applied SOLID through reusable architecture."
                        : "Documented professional experience supports this requirement.",
                    evidenceIds = new[] { Failure == "fake_evidence" ? "invented-id" : allowed.First(id => evidence[id].Contains(anchor)) }
                };
            }).ToArray();
            return Task.FromResult(new AiAssistantMessage(JsonSerializer.Serialize(new { results }), []));
        }
    }

    private sealed class TestContext(DbContextOptions<JobPilotDbContext> options) : JobPilotDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<JobApplication>().Property(item => item.JobDescription).HasColumnType("TEXT");
            modelBuilder.Entity<SavedResume>().Property(item => item.ResumeJson).HasColumnType("TEXT");
        }
    }
}
