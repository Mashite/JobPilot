using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JobPilot.Api.Data;
using JobPilot.Api.Data.Entities;
using JobPilot.Api.Endpoints;
using JobPilot.Api.Models;
using JobPilot.Api.Models.Agent;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Agent;
using JobPilot.Api.Services.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JobPilot.Tests;

public sealed class AgentWorkflowTests
{
    [Fact]
    public async Task WebWorkflowSavesAnalysisThenRetriesOnlyResumeAndReplaysCompletedResult()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<JobPilotDbContext>().UseSqlite(connection).Options;
        var provider = new WorkflowProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.Services.AddScoped<JobPilotDbContext>(_ => new WorkflowDb(options));
        builder.Services.AddSingleton<IAiModelProvider>(provider);
        builder.Services.AddSingleton(new AiProviderSelection("OpenAI", "test-model"));
        builder.Services.AddSingleton<SemanticMatchCache>();
        builder.Services.AddSingleton<AgentWorkflowGate>();
        builder.Services.AddSingleton<SkillNormalizer>();
        builder.Services.AddScoped<JobAnalysisService>();
        builder.Services.AddScoped<JobMatchingService>();
        builder.Services.AddScoped<SemanticJobMatcher>();
        builder.Services.AddScoped<JobMatchReviewStore>();
        builder.Services.AddScoped<ResumeGeneratorService>();
        builder.Services.AddScoped<CandidateProfileValidator>();
        builder.Services.AddScoped(services => new CandidateProfileService(builder.Environment, builder.Configuration,
            services.GetRequiredService<SkillNormalizer>(), services.GetRequiredService<JobPilotDbContext>()));
        builder.Services.AddScoped<AgentWorkflowService>();
        await using var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JobPilotDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.CareerProfiles.Add(new()
            {
                Id = 1, Version = "test",
                ProfileJson = JsonSerializer.Serialize(new CandidateProfile
                {
                    FullName = "Synthetic Engineer", ProfessionalTitle = "Software Engineer", Summary = "Builds maintained services.",
                    TechnicalSkills = ["C#"],
                    Experiences = [new WorkExperience
                    {
                        Company = "Synthetic Works", Position = "Engineer", StartDate = "2020", EndDate = "Present",
                        Responsibilities = ["Maintains C# services."]
                    }]
                }, JsonSerializerOptions.Web)
            });
            await db.SaveChangesAsync();
        }
        app.MapAgentWorkflows();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-JobPilot-Profile-Write", "1");
        var endpoint = $"api/agent/workflows/{Guid.NewGuid():D}";
        var input = new AgentRequest("Check this job, and create a CV", "Developer listing asking for C#.");

        var running = client.PostAsJsonAsync(endpoint, input);
        await provider.Drafting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var progress = await client.GetFromJsonAsync<AgentWorkflowView>(endpoint);
        Assert.Equal("generating_resume", progress!.Stage);
        Assert.Equal(100, progress.Analysis!.MatchScore);
        using var duplicate = await client.PostAsJsonAsync(endpoint, input);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        provider.ReleaseDraft.TrySetResult();
        using var first = await running;
        first.EnsureSuccessStatusCode();
        var failed = (await first.Content.ReadFromJsonAsync<AgentWorkflowView>())!;
        Assert.Equal("failed", failed.State);
        Assert.Equal("generating_resume", failed.Stage);
        Assert.True(failed.CanRetry);
        Assert.Null(failed.Resume);
        Assert.Equal(100, failed.Analysis!.MatchScore);
        using var incompleteDraft = await client.GetAsync(endpoint + "/resume");
        Assert.Equal(HttpStatusCode.NotFound, incompleteDraft.StatusCode);
        var savedAnalysis = await client.GetFromJsonAsync<JobMatchResult>(endpoint + "/analysis");
        Assert.Equal(["C#"], savedAnalysis!.MatchingSkills);

        using var resumed = await client.PostAsJsonAsync(endpoint, input);
        resumed.EnsureSuccessStatusCode();
        var completed = (await resumed.Content.ReadFromJsonAsync<AgentWorkflowView>())!;
        Assert.Equal("completed", completed.State);
        Assert.Equal(failed.Analysis.AnalysisId, completed.Analysis!.AnalysisId);
        Assert.Equal("ai-assistant/resume", completed.Resume!.EditorPath);
        Assert.Equal([AiOperation.Agent, AiOperation.JobAnalysis, AiOperation.ResumeGeneration, AiOperation.ResumeGeneration],
            provider.Operations);
        Assert.DoesNotContain(input.JobDescription!, provider.IntentPrompt);
        using var draft = JsonDocument.Parse(await client.GetStringAsync(endpoint + "/resume"));
        var draftJson = draft.RootElement.GetProperty("resume");
        Assert.Equal("Synthetic Works", draftJson.GetProperty("experiences")[0].GetProperty("company").GetString());
        Assert.Equal("C#", Assert.Single(draftJson.GetProperty("technicalSkills").EnumerateArray()).GetString());
        using var replay = await client.PostAsJsonAsync(endpoint, input);
        replay.EnsureSuccessStatusCode();
        Assert.Equal(4, provider.Operations.Count);

        using var scopeAfter = app.Services.CreateScope();
        var database = scopeAfter.ServiceProvider.GetRequiredService<JobPilotDbContext>();
        Assert.Equal(1, await database.AgentWorkflows.CountAsync());
        Assert.Equal(0, await database.JobApplications.CountAsync());
        Assert.Equal(0, await database.SavedResumes.CountAsync());

        provider.RequireSemantic = true;
        var semanticEndpoint = $"api/agent/workflows/{Guid.NewGuid():D}";
        using var semanticFailure = await client.PostAsJsonAsync(semanticEndpoint, input);
        semanticFailure.EnsureSuccessStatusCode();
        var partial = (await semanticFailure.Content.ReadFromJsonAsync<AgentWorkflowView>())!;
        Assert.Equal("completed", partial.Stage);
        Assert.Equal("partial", partial.Analysis!.Status);
        Assert.True(partial.CanRetry);
        Assert.NotNull(partial.Resume);
        Assert.Null(partial.Error);
        var pendingAnalysis = await client.GetFromJsonAsync<JobMatchResult>(semanticEndpoint + "/analysis");
        Assert.Contains("output token limit", pendingAnalysis!.SemanticFailure);

        provider.AllowSemantic = true;
        using var semanticRetry = await client.PostAsJsonAsync(semanticEndpoint, input);
        semanticRetry.EnsureSuccessStatusCode();
        Assert.Equal("completed", (await semanticRetry.Content.ReadFromJsonAsync<AgentWorkflowView>())!.State);
        Assert.Equal([AiOperation.Agent, AiOperation.JobAnalysis, AiOperation.SemanticMatching,
            AiOperation.SemanticMatching, AiOperation.ResumeGeneration, AiOperation.SemanticMatching], provider.Operations.Skip(4));

        provider.RequireSemantic = false;
        provider.LowCoverage = true;
        var lowScoreEndpoint = $"api/agent/workflows/{Guid.NewGuid():D}";
        using var lowScore = await client.PostAsJsonAsync(lowScoreEndpoint, input);
        lowScore.EnsureSuccessStatusCode();
        var draftDespiteGaps = (await lowScore.Content.ReadFromJsonAsync<AgentWorkflowView>())!;
        Assert.Equal(0, draftDespiteGaps.Analysis!.MatchScore);
        Assert.Equal(1, draftDespiteGaps.Analysis.MissingRequirements);
        Assert.NotNull(draftDespiteGaps.Resume);

        provider.ExtractionFailure = true;
        using var noAnalysis = await client.PostAsJsonAsync($"api/agent/workflows/{Guid.NewGuid():D}", input);
        noAnalysis.EnsureSuccessStatusCode();
        var draftWithoutAnalysis = (await noAnalysis.Content.ReadFromJsonAsync<AgentWorkflowView>())!;
        Assert.True(draftWithoutAnalysis.Analysis!.IsScoreProvisional);
        Assert.NotNull(draftWithoutAnalysis.Resume);
    }

    private sealed class WorkflowDb(DbContextOptions<JobPilotDbContext> options) : JobPilotDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<JobApplication>().Property(item => item.JobDescription).HasColumnType("TEXT");
            modelBuilder.Entity<SavedResume>().Property(item => item.ResumeJson).HasColumnType("TEXT");
        }
    }

    private sealed class WorkflowProvider : IAiModelProvider
    {
        public List<AiOperation> Operations { get; } = [];
        public TaskCompletionSource Drafting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDraft { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string IntentPrompt { get; private set; } = "";
        public bool RequireSemantic { get; set; }
        public bool AllowSemantic { get; set; }
        public bool LowCoverage { get; set; }
        public bool ExtractionFailure { get; set; }

        public async Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Operations.Add(request.Operation);
            switch (request.Operation)
            {
                case AiOperation.Agent:
                    IntentPrompt = Assert.IsType<AiUserMessage>(Assert.Single(request.History)).Text;
                    return new("""{"action":"analyze_and_resume"}""", []);
                case AiOperation.JobAnalysis when ExtractionFailure:
                    throw new AiProviderException("The extraction service is temporarily unavailable.", diagnosticCode: "temporary_provider_failure");
                case AiOperation.JobAnalysis when LowCoverage:
                    return new("""{"jobTitle":"Developer","requirements":[{"skill":"AWS","importance":"required","category":"technology"}]}""", []);
                case AiOperation.JobAnalysis:
                    return new(RequireSemantic
                        ? """{"jobTitle":"Developer","requirements":[{"skill":"Maintaining software services","importance":"required","category":"experience"}]}"""
                        : """{"jobTitle":"Developer","requirements":[{"skill":"C#","importance":"required","category":"technology"}]}""", []);
                case AiOperation.SemanticMatching when !AllowSemantic:
                    throw new AiProviderException("OpenAI reached this step’s output token limit.", diagnosticCode: "max_output_tokens");
                case AiOperation.SemanticMatching:
                    var prompt = Assert.IsType<AiUserMessage>(Assert.Single(request.History)).Text;
                    using (var json = JsonDocument.Parse(prompt))
                    {
                        var requirement = json.RootElement.GetProperty("requirements")[0];
                        return new(JsonSerializer.Serialize(new
                        {
                            results = new[]
                            {
                                new
                                {
                                    requirementId = requirement.GetProperty("requirementId").GetInt32(),
                                    classification = "SemanticMatch",
                                    reason = "The saved work supports maintenance of services.",
                                    evidenceIds = new[] { requirement.GetProperty("allowedEvidenceIds")[0].GetString() }
                                }
                            }
                        }), []);
                    }
                case AiOperation.ResumeGeneration when !ReleaseDraft.Task.IsCompleted:
                    Drafting.TrySetResult();
                    await ReleaseDraft.Task.WaitAsync(cancellationToken);
                    return new("""{"selectedSkills":["C#" """, []);
                case AiOperation.ResumeGeneration:
                    return new("""{"selectedSkills":["C#","Invented"],"selectedExperienceIndices":[0]}""", []);
                default:
                    throw new InvalidOperationException("Unexpected model operation.");
            }
        }
    }
}
