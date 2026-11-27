using System.Text.Json;
using JobPilot.Api.Data;
using JobPilot.Api.Data.Entities;
using JobPilot.Api.Models;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobPilot.Tests;

public sealed class CandidateIntelligenceTests
{
    [Fact]
    public async Task ProfessionalCapabilitiesCiteActualWorkWithoutRequiringIdenticalLabels()
    {
        using var cache = new SemanticMatchCache();
        var provider = new TestProvider();
        var normalizer = new SkillNormalizer();
        var matcher = new SemanticJobMatcher(new(normalizer), normalizer, provider, new("OpenAI", "mock"),
            cache, NullLogger<SemanticJobMatcher>.Instance);
        var result = await matcher.MatchAsync(Job("Database schema design", "Technical architecture leadership",
            "Production troubleshooting", "REST API development and integration", "Complex database development", "Data integrity"),
            new(Profile(), "v1"), default);

        Assert.All(result.RequirementMatches, row =>
        {
            Assert.Equal("semantic", row.Status);
            Assert.All(row.Citations!, citation => Assert.Equal("Synthetic Systems", citation.Employer));
        });
        Assert.Equal(100, result.MatchScore);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task ConfirmedFrameworkIsSuppliedAsContextAndCannotBeContradictedByAnotherQualification()
    {
        using var cache = new SemanticMatchCache();
        var provider = new TestProvider();
        provider.Respond = prompt =>
        {
            var requirement = prompt.GetProperty("requirements")[0];
            Assert.Equal("Data integrity", requirement.GetProperty("requirement").GetProperty("skill").GetString());
            Assert.Equal("Entity Framework Core", requirement.GetProperty("confirmedFacts")[0].GetString());
            return JsonSerializer.Serialize(new { results = new[]
            {
                new { requirementId = requirement.GetProperty("requirementId").GetInt32(), classification = "Missing",
                    reason = "Entity Framework Core is not documented.",
                    evidenceIds = new[] { requirement.GetProperty("allowedEvidenceIds")[0].GetString() } }
            } });
        };
        var normalizer = new SkillNormalizer();
        var matcher = new SemanticJobMatcher(new(normalizer), normalizer, provider, new("OpenAI", "mock"),
            cache, NullLogger<SemanticJobMatcher>.Instance);
        var job = Job("Entity Framework Core", "Data integrity");
        foreach (var item in job.Requirements)
            item.OriginalText = "Entity Framework Core, LINQ, ORM optimisation, transactions and data integrity.";
        var profile = Profile();
        profile.Experiences[0].Responsibilities[0] = "Reviewed data integrity as part of routine integration work.";
        var result = await matcher.MatchAsync(job, new(profile, "v1"), default);
        Assert.Equal("exact", result.RequirementMatches[0].Status);
        Assert.Equal("needs_verification", result.RequirementMatches[1].Status);
        Assert.DoesNotContain("not documented", result.RequirementMatches[1].Reason);
    }

    [Fact]
    public async Task MissingCapabilityGetsOneBroaderEvidencePass()
    {
        using var cache = new SemanticMatchCache();
        var profile = Profile();
        profile.Experiences[0].Responsibilities.AddRange(Enumerable.Range(1, 8)
            .Select(index => $"Owned service continuity for production deployment group {index}."));
        var evidenceCounts = new List<int>();
        var provider = new TestProvider
        {
            Respond = prompt =>
            {
                var requirement = prompt.GetProperty("requirements")[0];
                var evidence = requirement.GetProperty("allowedEvidenceIds");
                evidenceCounts.Add(evidence.GetArrayLength());
                var found = evidenceCounts.Count == 2;
                return JsonSerializer.Serialize(new { results = new[]
                {
                    new
                    {
                        requirementId = requirement.GetProperty("requirementId").GetInt32(),
                        classification = found ? "SemanticMatch" : "Missing",
                        reason = found ? "Documented operational ownership supports service continuity." : "Reviewed excerpts omit evidence.",
                        evidenceIds = found ? new[] { evidence[evidence.GetArrayLength() - 1].GetString() } : []
                    }
                } });
            }
        };
        var normalizer = new SkillNormalizer();
        var matcher = new SemanticJobMatcher(new(normalizer), normalizer, provider, new("OpenAI", "mock"),
            cache, NullLogger<SemanticJobMatcher>.Instance);
        var result = await matcher.MatchAsync(Job("Service continuity ownership"), new(profile, "v1"), default);
        Assert.Equal([4, 6], evidenceCounts);
        Assert.Equal("semantic", Assert.Single(result.RequirementMatches).Status);
        Assert.Equal(100, result.MatchScore);
    }

    [Fact]
    public async Task ExtractionPreservesSeparateAlternativesAndProficiencyFromOneClause()
    {
        var provider = new TestProvider
        {
            Respond = _ => """
                {"jobTitle":"Developer","requirements":[
                {"skill":"C#","alternativeGroup":"language","importance":"required","expectedProficiency":"advanced","critical":true,"originalText":"C# or Java and SQL Server or PostgreSQL"},
                {"skill":"Java","alternativeGroup":"language","importance":"required","originalText":"C# or Java and SQL Server or PostgreSQL"},
                {"skill":"SQL Server","alternativeGroup":"storage","importance":"required","originalText":"C# or Java and SQL Server or PostgreSQL"},
                {"skill":"PostgreSQL","alternativeGroup":"storage","importance":"required","originalText":"C# or Java and SQL Server or PostgreSQL"}]}
                """
        };
        var result = await new JobAnalysisService(provider, new SkillNormalizer()).ExtractRequirementsAsync("Synthetic listing");
        Assert.Equal(4, result.Requirements.Select(item => item.RequirementId).Distinct().Count());
        Assert.Equal(2, result.Requirements.Select(item => item.AlternativeGroup).Distinct().Count());
        Assert.Equal("advanced", result.Requirements[0].ExpectedProficiency);
        Assert.True(result.Requirements[0].Critical);
    }

    [Fact]
    public void AlternativesCountOnceAndEqualAreaWeightBalancesLongSkillLists()
    {
        var job = Job("C#", "Java", "Software architecture", "SQL Server", "Stored procedures", "Dynamic SQL", "Triggers", "Indexing");
        job.Requirements[0].AlternativeGroup = job.Requirements[1].AlternativeGroup = "language";
        job.Requirements[1].Critical = true;
        var profile = Profile();
        profile.TechnicalSkills.AddRange(["C#", "Software architecture", "SQL Server"]);
        var result = new JobMatchingService(new SkillNormalizer()).Match(job, profile);
        Assert.Equal(43, result.MatchScore);
        Assert.Equal(73, result.Suitability!.AreaCoverage);
        Assert.DoesNotContain("Java", result.Suitability.CriticalGaps);
        Assert.Contains("Stored procedures", result.Suitability.ImportantGaps);
        Assert.Equal("missing", result.RequirementMatches[1].Status);
    }

    [Fact]
    public async Task CandidateEvaluationAndDiscoveryUseVersionedSavedEvidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<JobPilotDbContext>().UseSqlite(connection).Options;
        var builder = WebApplication.CreateBuilder();
        var normalizer = new SkillNormalizer();
        var provider = new TestProvider
        {
            Respond = prompt =>
            {
                var fact = prompt.EnumerateArray().First(item =>
                    item.GetProperty("citation").GetProperty("excerpt").GetString()!.Contains("relational databases"));
                var id = fact.GetProperty("citation").GetProperty("id").GetString()!;
                Assert.Contains(prompt.EnumerateArray(), item => item.GetProperty("citation").GetProperty("kind").GetString() == "project");
                return JsonSerializer.Serialize(new
                {
                    seniority = "Senior engineering experience",
                    competencies = new[]
                    {
                        new { name = "Schema design", category = "Database engineering", description = "Designed databases.",
                            status = "semantic", evidenceIds = new[] { id } },
                        new { name = "Stored procedures", category = "Database engineering", description = "Inferred from SQL.",
                            status = "semantic", evidenceIds = new[] { id } },
                        new { name = "AWS", category = "DevOps and cloud", description = "Unsupported ID.",
                            status = "documented", evidenceIds = new[] { "fabricated-id" } }
                    }
                });
            }
        };
        await using (var writer = new TestContext(options))
        {
            await writer.Database.EnsureCreatedAsync();
            writer.CareerProfiles.Add(new CareerProfileRecord
            {
                Id = 1, Version = "v1", ProfileJson = JsonSerializer.Serialize(Profile(), JsonSerializerOptions.Web)
            });
            await writer.SaveChangesAsync();
            var profiles = new CandidateProfileService(builder.Environment, builder.Configuration, normalizer, writer);
            var snapshot = await profiles.GetSnapshotAsync();
            var evaluation = new CandidateEvaluationService(writer, provider, new("OpenAI", "mock"));
            var assessment = await evaluation.EvaluateAsync(snapshot, default);
            Assert.Equal("needs_verification", assessment.Competencies.Single(item => item.Name == "Stored procedures").AssessmentStatus);
            Assert.DoesNotContain(assessment.Competencies, item => item.Name == "AWS");
            Assert.All(assessment.Competencies, item => Assert.Equal("ai_assessed", item.VerificationStatus));
            Assert.Contains((await new ExperienceDiscoveryService(writer, profiles, normalizer).QuestionsAsync(snapshot, assessment, default)),
                question => question.Skill == "Stored procedures" && question.Answer is null);
        }
        await using var reader = new TestContext(options);
        var service = new CandidateProfileService(builder.Environment, builder.Configuration, normalizer, reader);
        var versionOne = await service.GetSnapshotAsync();
        var stored = new CandidateEvaluationService(reader, provider, new("OpenAI", "mock"));
        Assert.Equal("v1", (await stored.EvaluateAsync(versionOne, default)).ProfileVersion);
        Assert.Equal(1, provider.Calls);

        var discovery = new ExperienceDiscoveryService(reader, service, normalizer);
        Assert.True(await discovery.SaveAsync(versionOne, await stored.ReadAsync(versionOne, default),
        [
            new("Stored procedures", "", "professional", "Wrote settlement procedures for the billing system.", "Synthetic Systems", null),
            new("Triggers", "", "familiar", null, null, null),
            new("Docker", "", "personal_project", "Containerized the personal development project.", null, "Synthetic tracker")
        ], default));
        var updated = await service.GetSnapshotAsync();
        Assert.NotEqual(versionOne.Version, updated.Version);
        Assert.Null(await stored.ReadAsync(updated, default));
        Assert.Equal(["Docker", "Stored procedures"], updated.Profile.CareerEvidence.Select(item => item.OriginalName).Order().ToArray());
        Assert.All(updated.Profile.CareerEvidence, item => Assert.Equal(EvidenceVerification.ManuallyConfirmed, item.Verification));
        Assert.Contains(await discovery.QuestionsAsync(updated, null, default),
            question => question.Skill == "Triggers" && question.Answer?.Level == "familiar");
        var job = Job("Stored procedures", "Triggers", "Docker");
        job.Requirements[2].OriginalText = "Professional Docker experience.";
        var matched = new JobMatchingService(normalizer).Match(job, updated.Profile);
        Assert.Equal(["exact", "missing", "partial"], matched.RequirementMatches.Select(item => item.Status));
    }

    private static JobAnalysis Job(params string[] skills) => new()
    {
        Requirements = skills.Select(skill => new JobRequirement { Skill = skill, Category = "domain", OriginalText = skill }).ToList()
    };

    private static CandidateProfile Profile() => new()
    {
        FullName = "Synthetic Person", ProfessionalTitle = "Senior Engineer", TechnicalSkills = ["Entity Framework Core"],
        Experiences = [new()
        {
            Company = "Synthetic Systems", Position = "Engineer", StartDate = "2020", EndDate = "Present",
            Technologies = ["REST APIs"], Responsibilities =
            [
                "Designed relational databases from scratch and applied constraints to validate data integrity.",
                "Independently designed the architecture for the vehicle tracking platform.",
                "Troubleshot operational problems and diagnosed deployed factory systems.",
                "Developed REST APIs to integrate with enterprise ERP and SAP systems."
            ]
        }],
        ProjectDetails = [new ProfileProject { Name = "Synthetic tracker", Employer = "Synthetic Systems", Description = "Vehicle tracking." }]
    };

    private sealed class TestProvider : IAiModelProvider
    {
        public int Calls { get; private set; }
        public Func<JsonElement, string>? Respond { get; set; }
        public Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            var text = Assert.IsType<AiUserMessage>(Assert.Single(request.History)).Text;
            using var document = JsonDocument.Parse(text.StartsWith('{') || text.StartsWith('[') ? text : JsonSerializer.Serialize(text));
            return Task.FromResult(new AiAssistantMessage(Respond!(document.RootElement), []));
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
