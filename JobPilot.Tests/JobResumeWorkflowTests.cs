using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using JobPilot.Api.Data;
using JobPilot.Api.Endpoints;
using JobPilot.Api.Models;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Agent;
using JobPilot.Api.Services.Ai;
using JobPilot.Web.Components.Resume;
using JobPilot.Web.Models;
using JobPilot.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace JobPilot.Tests;

public sealed class JobResumeWorkflowTests : IDisposable
{
    private const string Listing = "Developer. Required C# and Docker. Preferred AWS.";
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("jobpilot-resume-flow-");

    [Fact]
    public async Task ReviewGenerationEditingVersioningAndExportUseTheSameJobAndVerifiedProfile()
    {
        var (app, ai) = await StartApiAsync();
        await using var server = app;
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var profileBefore = await client.GetFromJsonAsync<CandidateProfile>("/api/profile");
        Assert.Equal(["C#"], profileBefore!.TechnicalSkills);

        using var initial = await client.PostAsJsonAsync("/api/jobs/match", new { jobDescription = Listing });
        initial.EnsureSuccessStatusCode();
        var match = (await initial.Content.ReadFromJsonAsync<JobMatchResult>())!;
        var analysis = new { match.Requirements, match.JobTitle };
        using var saveVerified = new HttpRequestMessage(HttpMethod.Post, "/api/profile/skills")
        {
            Content = JsonContent.Create(new { analysis, confirmed = true, confirmedSkills = new[] { "Docker" } })
        };
        saveVerified.Headers.Add("X-JobPilot-Profile-Write", "1");
        using var savedSkill = await client.SendAsync(saveVerified);
        savedSkill.EnsureSuccessStatusCode();
        using var preview = await client.PostAsJsonAsync("/api/jobs/skill-review/preview",
            new { analysis, confirmed = true, confirmedSkills = new[] { "AWS" } });
        preview.EnsureSuccessStatusCode();
        var reviewed = (await preview.Content.ReadFromJsonAsync<JobMatchResult>())!;
        Assert.True(reviewed.MatchScore > match.MatchScore);

        var reviewState = new SkillReviewState(["Docker", "AWS"]);
        Assert.True(reviewState.Stage("Docker"));
        Assert.True(reviewState.Stage("AWS"));
        reviewState.SetEvidence("Docker", "Verified bullet");
        reviewState.SetEvidence("AWS", "Unsupported review note");
        reviewState.Confirm();
        reviewState.AcceptSavedMatches(["Docker"]);
        using var generated = await client.PostAsJsonAsync("/api/resumes/generate", new
        {
            jobDescription = Listing,
            confirmedSkills = reviewState.ResumeSkillHints.ToArray(),
            confirmedEvidence = reviewState.EvidenceFor(reviewState.ResumeSkillHints).Values.ToArray()
        });
        generated.EnsureSuccessStatusCode();
        var draft = (await generated.Content.ReadFromJsonAsync<GeneratedResume>())!;
        Assert.Equal(["Docker", "C#"], draft.TechnicalSkills);
        Assert.Equal("Example Company", Assert.Single(draft.Experiences).Company);
        Assert.Equal(["Verified project"], draft.Projects);
        Assert.Contains("Verified certification", Assert.Single(draft.Certifications));
        Assert.Contains("verifiedReviewSkills", ai.ResumePrompt);
        using var generatorInput = JsonDocument.Parse(ai.ResumePrompt);
        Assert.Equal("Docker", Assert.Single(generatorInput.RootElement.GetProperty("verifiedReviewSkills").EnumerateArray()).GetString());
        Assert.Equal(0, Assert.Single(generatorInput.RootElement.GetProperty("verifiedExperienceIndices").EnumerateArray()).GetInt32());
        Assert.DoesNotContain("Unsupported review note", ai.ResumePrompt);

        var workflowDraft = new JobResumeDraft();
        workflowDraft.Open(draft);
        draft.ProfessionalSummary = "Edited summary";
        draft.Experiences[0].Highlights[0] = "Edited bullet";
        draft.Education[0] = "Edited education";
        draft.Certifications[0] = "Edited certification";
        workflowDraft.Review();
        Assert.False(workflowDraft.IsEditing);
        Assert.Equal(["AWS"], reviewState.Confirmed);
        workflowDraft.Open(workflowDraft.Resume!);
        Assert.True(workflowDraft.IsEditing);
        Assert.Same(draft, workflowDraft.Resume);
        await AssertEditorRendersDraft(draft);

        var applicationPayload = new
        {
            jobTitle = "Developer", companyName = "Hiring Company", jobDescription = Listing,
            matchScore = reviewed.MatchScore, resume = draft, reuseExisting = true
        };
        var submissions = await Task.WhenAll(
            client.PostAsJsonAsync("/api/applications", applicationPayload),
            client.PostAsJsonAsync("/api/applications", applicationPayload));
        using var applicationResponse = submissions[0];
        using var duplicateResponse = submissions[1];
        var firstApplication = await ReadId(applicationResponse);
        Assert.Equal(firstApplication, await ReadId(duplicateResponse));
        var listPath = $"/api/applications/{firstApplication}/resumes";
        var originalVersion = Assert.Single((await client.GetFromJsonAsync<List<ResumeVersion>>(listPath))!);

        draft.ProfessionalSummary = "Edited summary for version two";
        var js = new DownloadCapture();
        var workflow = new ResumeWorkflow(js);
        var saves = await Task.WhenAll(
            workflow.SaveAsync(client, firstApplication, draft, CancellationToken.None),
            workflow.SaveAsync(client, firstApplication, draft, CancellationToken.None));
        Assert.Equal(saves[0].Id, saves[1].Id);
        Assert.Single(saves, result => result.Created);
        var versions = (await workflow.ListVersionsAsync(client, firstApplication, CancellationToken.None));
        Assert.Equal([saves[0].Id, originalVersion.Id], versions.Select(version => version.Id));
        var old = await client.GetFromJsonAsync<GeneratedResume>($"{listPath}/{originalVersion.Id}");
        var latest = await client.GetFromJsonAsync<GeneratedResume>($"{listPath}/latest");
        Assert.Equal("Edited summary", old!.ProfessionalSummary);
        Assert.Equal(draft.ProfessionalSummary, latest!.ProfessionalSummary);

        await workflow.DownloadAsync(client, draft, CancellationToken.None);
        Assert.Equal("JobPilotResume.docx", js.FileName);
        using var document = WordprocessingDocument.Open(new MemoryStream(js.Bytes!), false);
        var body = Assert.IsType<Body>(document.MainDocumentPart?.Document?.Body);
        Assert.Contains("Edited summary for version two", body.InnerText);
        Assert.Contains("Edited bullet", body.InnerText);
        Assert.Contains("Edited education", body.InnerText);
        Assert.Contains("CERTIFICATIONS", body.InnerText);
        Assert.Contains("Edited certification", body.InnerText);
        Assert.Empty(body.Descendants<Table>());
        Assert.Empty(body.Descendants<Drawing>());
        Assert.Contains(body.Elements<Paragraph>(), paragraph => paragraph.InnerText == "CERTIFICATIONS" &&
            paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "Heading1");
    }

    private static async Task<int> ReadId(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var details = await response.Content.ReadFromJsonAsync<JsonElement>();
        return details.GetProperty("id").GetInt32();
    }

    private static async Task AssertEditorRendersDraft(GeneratedResume draft)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { ["Resume"] = draft });
            var component = await renderer.RenderComponentAsync<ResumeEditor>(parameters);
            return component.ToHtmlString();
        });
        Assert.Contains("Resume Editor", html);
        Assert.Contains("Edited summary", html);
        Assert.Contains("Edited certification", html);
    }

    private async Task<(WebApplication App, WorkflowAi Ai)> StartApiAsync()
    {
        var profile = Path.Combine(directory.FullName, "candidate-profile.json");
        await File.WriteAllTextAsync(profile, JsonSerializer.Serialize(new CandidateProfile
        {
            FullName = "Synthetic Candidate",
            ProfessionalTitle = "Developer",
            Summary = "Verified summary",
            TechnicalSkills = ["C#"],
            Projects = ["Verified project"],
            Education = ["Verified education"],
            Certifications = [new() { Name = "Verified certification", Issuer = "Example board" }],
            Experiences = [new()
            {
                Company = "Example Company", Position = "Developer", StartDate = "2020", EndDate = "Present",
                Responsibilities = ["Verified bullet"]
            }]
        }, JsonSerializerOptions.Web));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory.FullName });
        builder.Configuration["CandidateProfile:Path"] = profile;
        builder.Logging.ClearProviders();
        var ai = new WorkflowAi();
        builder.Services.AddSingleton<IAiModelProvider>(ai);
        builder.Services.AddSingleton(new AiProviderSelection("OpenAI", "mock-model"));
        builder.Services.AddSingleton<SkillNormalizer>();
        builder.Services.AddSingleton<CandidateProfileService>();
        builder.Services.AddScoped<CandidateProfileValidator>();
        builder.Services.AddScoped<JobMatchingService>();
        builder.Services.AddScoped<JobAnalysisService>();
        builder.Services.AddScoped<WordDocumentService>();
        builder.Services.AddScoped<ResumeGeneratorService>();
        builder.Services.AddScoped<JobPilotAgentService>();
        builder.Services.AddHttpClient();
        var database = Path.Combine(directory.FullName, "resume-test.db");
        var options = new DbContextOptionsBuilder<JobPilotDbContext>()
            .UseSqlite($"Data Source={database};Pooling=False;Default Timeout=15").Options;
        builder.Services.AddScoped<JobPilotDbContext>(_ => new ResumeTestDbContext(options));
        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<JobPilotDbContext>().Database.EnsureCreatedAsync();
        app.MapJobPilotEndpoints();
        app.MapProfileEndpoints();
        app.MapSkillReviewEndpoints();
        app.MapJobApplicationEndpoints();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        return (app, ai);
    }

    public void Dispose()
    {
        foreach (var file in directory.EnumerateFiles())
            file.Delete();
        directory.Delete();
    }

    private sealed class ResumeTestDbContext(DbContextOptions<JobPilotDbContext> options) : JobPilotDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<JobPilot.Api.Data.Entities.JobApplication>().Property(job => job.JobDescription).HasColumnType("TEXT");
            modelBuilder.Entity<JobPilot.Api.Data.Entities.SavedResume>().Property(resume => resume.ResumeJson).HasColumnType("TEXT");
        }
    }

    private sealed class WorkflowAi : IAiModelProvider
    {
        public string ResumePrompt { get; private set; } = "";

        public Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            if (request.OutputSchema!.Value.GetProperty("properties").TryGetProperty("requirements", out _))
            {
                return Task.FromResult(new AiAssistantMessage("""
                    {"jobTitle":"Developer","requirements":[{"skill":"C#","importance":"required"},{"skill":"Docker","importance":"required"},{"skill":"AWS","importance":"preferred"}]}
                    """, []));
            }
            ResumePrompt = Assert.IsType<AiUserMessage>(Assert.Single(request.History)).Text;
            return Task.FromResult(new AiAssistantMessage("""
                {"selectedSkills":["C#","AWS"],"selectedExperienceIndices":[99]}
                """, []));
        }
    }

    private sealed class DownloadCapture : IJSRuntime
    {
        public string? FileName { get; private set; }
        public byte[]? Bytes { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("jobPilot.downloadFile", identifier);
            FileName = Assert.IsType<string>(args![0]);
            Bytes = Convert.FromBase64String(Assert.IsType<string>(args[2]));
            return ValueTask.FromResult(default(T)!);
        }
    }
}
