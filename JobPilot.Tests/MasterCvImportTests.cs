using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using JobPilot.Api.Data;
using JobPilot.Api.Data.Entities;
using JobPilot.Api.Endpoints;
using JobPilot.Api.Models;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace JobPilot.Tests;

public sealed class MasterCvImportTests
{
    [Fact]
    public void NestedSectionsKeepTechnologiesProjectsAndCompetenciesInTheirOwnRoles()
    {
        using var source = new MemoryStream(CreateDocument());
        var profile = new MasterCvImporter(new SkillNormalizer()).Parse(source);
        Assert.Equal("Builds manufacturing applications.\n\nImproves production reliability.\nWorks with operators.",
            profile.Summary);
        Assert.Equal(["Process Modeling", "  State diagrams"], profile.CoreStrengths);
        Assert.Contains(profile.CareerEvidence, item => item.OriginalName == "A/B Protocol" &&
            item.Category == "Industrial & Communication Technologies");
        Assert.Contains(profile.CareerEvidence, item => item.OriginalName == "Build Toolkit" &&
            item.Category == "Development & Collaboration");
        Assert.Contains(profile.CareerEvidence, item => item.OriginalName == "Process Modeling" &&
            item.Category == "Professional competencies");
        Assert.Equal("Synthetic Town", profile.Experiences[0].Location);
        Assert.True(profile.Experiences[0].IsCurrentPosition);
        Assert.Equal(["C#", "TCP/IP", "Microsoft SQL Server"], profile.Experiences[0].Technologies);
        Assert.Equal(["WCF", "A/B Protocol"], profile.Experiences[1].Technologies);
        Assert.Contains("Reduced queue delays.", profile.Experiences[0].Achievements);
        Assert.DoesNotContain(profile.Experiences[0].Responsibilities, line => line.Contains("TCP/IP", StringComparison.Ordinal));
        Assert.Collection(profile.ProjectDetails!,
            project =>
            {
                Assert.Equal("Dispatch", project.Name);
                Assert.Equal("Synthetic Works", project.Employer);
                Assert.Contains("Architecture & Product Design", project.Description);
                Assert.Contains("Reduced queue delays.", project.Achievements);
            },
            project =>
            {
                Assert.Equal("Signal — Reporting Platform", project.Name);
                Assert.Equal("Earlier Works", project.Employer);
            });
        Assert.Contains(profile.ImportReviewSections, section =>
            section.Kind == "themes" && section.Lines.Contains("Program Ownership"));
        Assert.Contains(profile.ImportReviewSections, section =>
            section.Kind == "philosophy" && section.Lines.Contains("Listen before designing."));
    }

    [Fact]
    public void LocalDocumentKeepsChronologyTablesAndEvidence()
    {
        using var source = new MemoryStream(CreateDocument());
        var profile = new MasterCvImporter(new SkillNormalizer()).Parse(source);

        Assert.Collection(profile.Experiences,
            recent => { Assert.Equal("Synthetic Works", recent.Company); Assert.Equal("2020", recent.StartDate); Assert.Equal("Present", recent.EndDate); },
            earlier => { Assert.Equal("Earlier Works", earlier.Company); Assert.Equal("2010", earlier.StartDate); Assert.Equal("2014", earlier.EndDate); });
        Assert.Equal("2010", Assert.Single(profile.EducationDetails!).GraduationDate);
        Assert.Contains(profile.CareerEvidence, evidence => evidence.OriginalName == "C#" &&
            evidence.Employer == "Synthetic Works" && evidence.Project == "Dispatch" &&
            evidence.Classification == EvidenceClassification.ExplicitlyDocumented &&
            evidence.Verification == EvidenceVerification.Unverified);
        Assert.Contains(profile.CareerEvidence, evidence => evidence.OriginalName == "SQL Server" && evidence.Employer == "Earlier Works");
        Assert.DoesNotContain(profile.CareerEvidence, evidence => evidence.OriginalName == "AWS");
        Assert.Contains(profile.ImportReviewSections, section => section.RequiresReview && section.Heading == "UNSORTED NOTES");
    }

    [Fact]
    public async Task ImportRequiresConfirmationAndVersionAndPersistsAcrossContexts()
    {
        using var legacy = new LegacyProfileFile();
        using var sqlite = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync();
        var options = new DbContextOptionsBuilder<JobPilotDbContext>().UseSqlite(sqlite).Options;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<SkillNormalizer>();
        builder.Services.AddScoped<JobPilotDbContext>(_ => new TestContext(options));
        builder.Services.AddScoped<MasterCvImporter>();
        builder.Services.AddScoped<MasterCvMerge>();
        builder.Services.AddScoped<CandidateProfileValidator>();
        builder.Services.AddScoped<JobMatchingService>();
        builder.Services.AddScoped(services => new CandidateProfileService(
            builder.Environment, new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["CandidateProfile:Path"] = legacy.Path }).Build(),
            services.GetRequiredService<SkillNormalizer>(), services.GetRequiredService<JobPilotDbContext>()));

        await using var app = builder.Build();
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<JobPilotDbContext>().Database.EnsureCreatedAsync();
        app.MapProfileEndpoints();
        app.MapMasterCvImport();
        app.MapSkillReviewEndpoints();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-JobPilot-Profile-Write", "1");

        using var upload = new ByteArrayContent(CreateDocument());
        using var preview = await client.PostAsync("/api/profile/import/preview", upload);
        preview.EnsureSuccessStatusCode();
        var version = preview.Headers.ETag!.ToString();
        var previewJson = await preview.Content.ReadAsStringAsync();
        var parsedPreview = JsonSerializer.Deserialize<ImportPreview>(previewJson, JsonSerializerOptions.Web)!;
        var draft = parsedPreview.Profile;
        var previewForm = JsonSerializer.Deserialize<ImportPreviewForm>(previewJson, JsonSerializerOptions.Web)!.Profile;
        var diagnostics = parsedPreview.Diagnostics;
        Assert.Equal(2, diagnostics.GetProperty("employmentRecords").GetInt32());
        Assert.Equal([3, 2], diagnostics.GetProperty("employerTechnologyCounts").EnumerateArray().Select(count => count.GetInt32()));
        Assert.DoesNotContain("Synthetic Works", diagnostics.GetRawText());
        Assert.Equal(draft.Summary, previewForm.Summary);
        Assert.Equal(draft.Experiences[0].Technologies, previewForm.Experiences[0].Technologies);
        Assert.Equal(draft.ProjectDetails![0].Employer, previewForm.ProjectDetails![0].Employer);
        var before = await client.GetFromJsonAsync<CandidateProfile>("/api/profile");
        Assert.Empty(before!.Experiences);
        Assert.Equal("Existing profile summary.", before.Summary);
        Assert.All(draft.CareerEvidence, evidence => Assert.Equal(EvidenceVerification.Unverified, evidence.Verification));
        using var declined = await Confirm(client, draft, version, false);
        Assert.Equal(HttpStatusCode.BadRequest, declined.StatusCode);

        draft.CareerEvidence = draft.CareerEvidence.Select(evidence => evidence.OriginalName is "C#" or "Application architecture" or "WCF"
            ? evidence with { Verification = EvidenceVerification.ImportConfirmed } : evidence).ToList();
        using var confirmed = await Confirm(client, draft, version, true);
        confirmed.EnsureSuccessStatusCode();
        using var stale = await Confirm(client, draft, version, true);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var repeated = await Confirm(client, draft, confirmed.Headers.ETag!.ToString(), true, replaceProfile: true);
        repeated.EnsureSuccessStatusCode();

        var saved = await client.GetFromJsonAsync<CandidateProfile>("/api/profile");
        Assert.Equal(2, saved!.Experiences.Count);
        Assert.Equal(draft.Summary, saved.Summary);
        Assert.Contains("Existing profile summary.", File.ReadAllText(legacy.Path));
        Assert.Equal(draft.Experiences[1].Technologies, saved.Experiences[1].Technologies);
        Assert.Equal(2, saved.ProjectDetails!.Count);
        var savedForm = (await client.GetFromJsonAsync<JobPilot.Web.Models.CandidateProfileForm>("/api/profile"))!;
        Assert.Equal(draft.Summary, savedForm.Summary);
        Assert.Equal("Synthetic Works", savedForm.ProjectDetails![0].Employer);
        Assert.Contains(savedForm.ImportReviewSections, section => section.Kind == "themes");
        Assert.Equal(draft.CareerEvidence.Count, saved.CareerEvidence.Count);
        var analysis = new JobAnalysis
        {
            Requirements = new[] { "C#", "Software architecture", "Software design patterns", "AWS" }
                .Select(skill => new JobRequirement { Skill = skill, Importance = "required", Category = "technology" }).ToList()
        };
        var matching = new JobMatchingService(new SkillNormalizer());
        var result = matching.Match(analysis, saved);
        Assert.Equal(["exact", "equivalent", "related", "missing"], result.RequirementMatches.Select(match => match.Status));
        Assert.Equal(50, result.MatchScore);
        Assert.Equal("Synthetic Works", result.RequirementMatches[0].EvidenceEmployer);
        var wcf = matching.Match(new JobAnalysis
        {
            Requirements = [new JobRequirement { Skill = "WCF Services", Category = "technology" }]
        }, saved);
        Assert.Equal("Earlier Works", Assert.Single(wcf.RequirementMatches).EvidenceEmployer);

        using var ownEvidence = await client.PostAsJsonAsync("/api/jobs/skill-review/preview", new
        {
            analysis, confirmed = true, relatedSkills = new[] { "AWS" },
            confirmedEvidence = new Dictionary<string, string> { ["AWS"] = "Comparable deployment workflow." }
        });
        ownEvidence.EnsureSuccessStatusCode();
        Assert.Equal("related", (await ownEvidence.Content.ReadFromJsonAsync<JobMatchResult>())!.RequirementMatches[^1].Status);
        Assert.Equal("missing", matching.Match(analysis, saved).RequirementMatches[^1].Status);

        using var markMissing = await client.PostAsJsonAsync("/api/jobs/skill-review/preview", new
        {
            analysis, confirmed = true, missingSkills = new[] { "Software design patterns" }
        });
        markMissing.EnsureSuccessStatusCode();
        Assert.Equal("missing", (await markMissing.Content.ReadFromJsonAsync<JobMatchResult>())!.RequirementMatches[2].Status);

        var ai = new ResumeAi();
        var resume = await new ResumeGeneratorService(ai, new SkillNormalizer()).GenerateAsync(
            "C# software engineer AWS", saved, includeAllExperiences: true);
        Assert.Equal(["C#"], resume.TechnicalSkills);
        Assert.Equal(2, resume.Experiences.Count);
        Assert.Contains("Builds manufacturing applications.", resume.ProfessionalSummary);
        Assert.DoesNotContain("Synthetic Person", ai.Prompt);
        Assert.DoesNotContain("Synthetic Works", ai.Prompt);

        var qualification = new JobRequirement { Skill = "Industrial workflow design", Category = "other", Importance = "required" };
        using var savedReview = await client.PostAsJsonAsync("/api/profile/skills", new
        {
            analysis = new { requirements = new[] { qualification } }, confirmed = true, confirmCompetencies = true,
            confirmedSkills = new[] { qualification.Skill },
            confirmedEvidence = new Dictionary<string, string> { [qualification.Skill] = "Built a dispatch application in C#." }
        });
        savedReview.EnsureSuccessStatusCode();
        var recalculated = await savedReview.Content.ReadFromJsonAsync<JobMatchResult>();
        Assert.Equal(100, recalculated!.MatchScore);
        var savedCompetency = Assert.Single((await client.GetFromJsonAsync<CandidateProfile>("/api/profile"))!.CareerEvidence,
            entry => entry.OriginalName == qualification.Skill);
        Assert.Equal(EvidenceVerification.ManuallyConfirmed, savedCompetency.Verification);
        Assert.Equal("Synthetic Works", savedCompetency.Employer);
    }

    [Fact]
    public async Task RepeatedRequirementExtractionUsesLocalCache()
    {
        using var cache = new JobRequirementCache();
        var ai = new AnalysisAi();
        var service = new JobAnalysisService(ai, new SkillNormalizer(), cache);
        Assert.Single((await service.ExtractRequirementsAsync("Listing needs C#.")).Requirements);
        Assert.Single((await service.ExtractRequirementsAsync("Listing needs C#.")).Requirements);
        Assert.Equal(1, ai.Requests);
        await service.ExtractRequirementsAsync("A different listing.");
        Assert.Equal(2, ai.Requests);
    }

    [Fact]
    public void SqlServerMigrationAddsOnlyTheCareerProfileTable()
    {
        var options = new DbContextOptionsBuilder<JobPilotDbContext>()
            .UseSqlServer("Server=.;Database=JobPilotTest;Integrated Security=true;TrustServerCertificate=true").Options;
        using var db = new JobPilotDbContext(options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrations = db.Database.GetMigrations().ToArray();
        var migrator = db.GetService<IMigrator>();
        var script = migrator.GenerateScript(migrations[0], "20261123120000_AddCareerProfile");
        Assert.Contains("CREATE TABLE [CareerProfiles]", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("ALTER TABLE", script);
        var reviewScript = migrator.GenerateScript("20261123120000_AddCareerProfile", "20261125120000_AddJobMatchReviews");
        Assert.Contains("CREATE TABLE [JobMatchReviews]", reviewScript);
        Assert.DoesNotContain("DROP TABLE", reviewScript);
        Assert.DoesNotContain("ALTER TABLE", reviewScript);
        var workflowScript = migrator.GenerateScript("20261125120000_AddJobMatchReviews", "20261126120000_AddAgentWorkflows");
        Assert.Contains("CREATE TABLE [AgentWorkflows]", workflowScript);
        Assert.DoesNotContain("ALTER TABLE", workflowScript);
        var intelligenceScript = migrator.GenerateScript("20261126120000_AddAgentWorkflows", migrations[^1]);
        Assert.Contains("CREATE TABLE [CandidateIntelligence]", intelligenceScript);
        Assert.Contains("CREATE TABLE [ExperienceDiscovery]", intelligenceScript);
        Assert.DoesNotContain("DROP TABLE", intelligenceScript);
    }

    private static async Task<HttpResponseMessage> Confirm(HttpClient client, CandidateProfile draft, string version, bool confirmed,
        bool replaceProfile = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/profile/import/confirm")
        {
            Content = JsonContent.Create(new { profile = draft, confirmed, replaceProfessionalProfile = replaceProfile })
        };
        request.Headers.TryAddWithoutValidation("If-Match", version);
        return await client.SendAsync(request);
    }

    private static byte[] CreateDocument()
    {
        using var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var body = new Body();
            foreach (var line in new[]
            {
                "Synthetic Person", "Software Engineer", "PROFESSIONAL PROFILE", "Builds manufacturing applications."
            })
                body.Append(Paragraph(line));
            body.Append(new Paragraph(new Run(new Text("Improves production reliability."), new Break(),
                new Text("Works with operators."))));
            body.Append(Heading("CORE STRENGTHS"), Bullet("Process Modeling", 0), Bullet("State diagrams", 1));
            body.Append(Heading("TECHNICAL PROFILE"), Heading("Industrial & Communication Technologies"),
                Paragraph("A/B Protocol, Vendor Transport"), Heading("Development & Collaboration"), Paragraph("Build Toolkit"));
            body.Append(Heading("PROFESSIONAL EXPERIENCE"), Heading("Synthetic Works — Synthetic Town"),
                Paragraph("Software Engineer"), Paragraph("2020–Present"), Paragraph("Project: Dispatch"),
                Heading("Architecture & Product Design"), Paragraph("Built a dispatch application in C#."),
                Paragraph("Application architecture for operations."), Paragraph("No AWS experience."),
                Heading("Business Impact"), Paragraph("Reduced queue delays."), Heading("Core Technologies"),
                Paragraph("C#, TCP/IP, Microsoft SQL Server, SQL Server"));
            body.Append(Heading("Earlier Works"), Paragraph("Software Engineer"), Paragraph("2010–2014"),
                Heading("Projects"), Bullet("Signal Reporting System", 0),
                Heading("Signal — Reporting Platform"));
            body.Append(new Table(new TableRow(new TableCell(Paragraph("Designed reporting")), new TableCell(Paragraph("SQL Server")))));
            body.Append(Paragraph("Core Technologies: WCF, A/B Protocol"), Heading("EDUCATION"),
                Paragraph("Bachelor of Engineering"), Paragraph("Synthetic University | 2006–2010"),
                Heading("UNSORTED NOTES"), Paragraph("Needs manual review."),
                Heading("PROFESSIONAL THEMES"), Heading("Program Ownership"), Paragraph("Maintain durable processes."),
                Heading("ENGINEERING PHILOSOPHY"), Paragraph("Listen before designing."));
            word.AddMainDocumentPart().Document = new Document(body);
        }
        return stream.ToArray();
    }

    private static Paragraph Paragraph(string text) => new(new Run(new Text(text)));
    private static Paragraph Heading(string text) => new(new Run(new RunProperties(new Bold()), new Text(text)));
    private static Paragraph Bullet(string text, int depth) => new(
        new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = depth }, new NumberingId { Val = 1 })),
        new Run(new Text(text)));

    private sealed record ImportPreview(CandidateProfile Profile, JsonElement Diagnostics);
    private sealed record ImportPreviewForm(JobPilot.Web.Models.CandidateProfileForm Profile);

    private sealed class LegacyProfileFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");

        public LegacyProfileFile() => File.WriteAllText(Path,
            """{"fullName":"Synthetic Person","summary":"Existing profile summary."}""");

        public void Dispose() => File.Delete(Path);
    }

    private sealed class TestContext(DbContextOptions<JobPilotDbContext> options) : JobPilotDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<JobApplication>().Property(job => job.JobDescription).HasColumnType("TEXT");
            modelBuilder.Entity<SavedResume>().Property(resume => resume.ResumeJson).HasColumnType("TEXT");
        }
    }

    private sealed class ResumeAi : IAiModelProvider
    {
        public string Prompt { get; private set; } = "";

        public Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Prompt = Assert.IsType<AiUserMessage>(Assert.Single(request.History)).Text;
            return Task.FromResult(new AiAssistantMessage("""{"selectedSkills":["C#","AWS"],"selectedExperienceIndices":[0]}""", []));
        }
    }

    private sealed class AnalysisAi : IAiModelProvider
    {
        public int Requests { get; private set; }

        public Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Requests++;
            return Task.FromResult(new AiAssistantMessage("""{"jobTitle":"Engineer","requirements":[{"skill":"C#","category":"technology","importance":"required"}]}""", []));
        }
    }
}
