using System.Text.Json;
using JobPilot.Api.Models;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobPilot.Tests;

public sealed class SemanticBatchTests
{
    [Fact]
    public async Task TokenLimitedBatchSplitsAndRestartsWithOnlyPendingRequirements()
    {
        using var cache = new SemanticMatchCache();
        var mock = new BatchProvider();
        mock.Fail = (batch, ids) => batch.Number == 2 && (batch.Retry == 0 || ids.Contains(4))
            ? "max_output_tokens" : null;
        var progress = new List<SemanticProgress>();
        var checkpoints = new List<JobMatchResult>();

        var first = await Matcher(mock, cache).MatchAsync(Analysis(6), Snapshot(), default,
            semanticStarted: step => { progress.Add(step); return Task.CompletedTask; },
            saveCheckpoint: result => { checkpoints.Add(result); return Task.CompletedTask; });

        Assert.Equal(new[] { "1,2", "3,4", "3", "4", "5,6" }, mock.Calls.Select(call => string.Join(',', call.Ids)));
        Assert.Equal(new[] { 0, 0, 1, 1, 0 }, mock.Calls.Select(call => call.Info.Retry));
        Assert.Equal(new[] { 1, 2, 2, 2, 3, 0 }, progress.Select(step => step.Batch));
        Assert.Equal([43, 57, 86], checkpoints.Select(result => result.MatchScore));
        Assert.All(mock.Calls, call =>
        {
            Assert.InRange(call.Info.RequirementCount, 1, 2);
            Assert.InRange(call.Info.EvidenceCount, 1, 8);
            Assert.Equal(8192, call.Limit);
        });
        Assert.Equal("max_output_tokens", Assert.Single(first.SemanticBatchFailures).Code);
        Assert.Contains("Batch 2 of 3", first.SemanticFailure);
        Assert.Equal("pending_semantic", first.RequirementMatches[4].Status);
        Assert.Equal("exact", first.RequirementMatches[0].Status);
        Assert.Equal(86, first.MatchScore);
        Assert.True(first.IsScoreProvisional);

        // Recover the persisted checkpoint with a new process-local cache.
        var saved = JsonSerializer.Deserialize<JobMatchResult>(JsonSerializer.Serialize(first))!;
        using var restartedCache = new SemanticMatchCache();
        var resumed = new BatchProvider();
        var second = await Matcher(resumed, restartedCache).MatchAsync(Analysis(6), Snapshot(), default, previous: saved);

        Assert.Equal([4], Assert.Single(resumed.Calls).Ids);
        Assert.Equal(100, second.MatchScore);
        Assert.False(second.IsScoreProvisional);
        Assert.Equal("completed", second.SemanticStatus);
        Assert.Empty(second.SemanticBatchFailures);
        Assert.Empty(second.Warnings);
    }

    [Fact]
    public async Task SingletonTokenLimitIncreasesTheBudgetOnce()
    {
        using var cache = new SemanticMatchCache();
        var mock = new BatchProvider { Fail = (_, _) => "max_output_tokens" };
        var result = await Matcher(mock, cache).MatchAsync(Analysis(1), Snapshot(), default);
        Assert.Equal([8192, 12000], mock.Calls.Select(call => call.Limit));
        Assert.True(result.IsScoreProvisional);
        Assert.Equal(50, result.MatchScore);
    }

    [Fact]
    public async Task ContentFilterDoesNotRetryAndLaterBatchStillCompletes()
    {
        using var cache = new SemanticMatchCache();
        var mock = new BatchProvider { Fail = (info, _) => info.Number == 1 ? "content_filter" : null };
        var result = await Matcher(mock, cache).MatchAsync(Analysis(3), Snapshot(), default);
        Assert.Equal(2, mock.Calls.Count);
        Assert.All(mock.Calls, call => Assert.Equal(0, call.Info.Retry));
        Assert.Equal("content_filter", Assert.Single(result.SemanticBatchFailures).Code);
        Assert.Equal("semantic", result.RequirementMatches[3].Status);
        Assert.True(result.IsScoreProvisional);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("invalid_evidence")]
    [InlineData("invalid_json")]
    public async Task InvalidResponsesLeaveOnlyUnvalidatedRowsPending(string problem)
    {
        using var cache = new SemanticMatchCache();
        var mock = new BatchProvider { Fail = (info, _) => info.Number == 1 ? problem : null };
        var matcher = Matcher(mock, cache);
        var first = await matcher.MatchAsync(Analysis(3), Snapshot(), default);
        Assert.True(first.IsScoreProvisional);
        Assert.Equal("pending_semantic", first.RequirementMatches[2].Status);
        Assert.Equal("semantic", first.RequirementMatches[3].Status);
        if (problem != "invalid_json")
            Assert.Equal("semantic", first.RequirementMatches[1].Status);
        Assert.Equal(problem == "invalid_json" ? "invalid_json" : "invalid_results", Assert.Single(first.SemanticBatchFailures).Code);

        mock.Fail = (_, _) => null;
        var second = await matcher.MatchAsync(Analysis(3), Snapshot(), default);
        Assert.Equal(problem == "invalid_json" ? new[] { 1, 2 } : new[] { 2 }, mock.Calls[^1].Ids);
        Assert.False(second.IsScoreProvisional);
        Assert.Equal(100, second.MatchScore);
    }

    private static SemanticJobMatcher Matcher(BatchProvider provider, SemanticMatchCache cache)
    {
        var normalizer = new SkillNormalizer();
        return new(new JobMatchingService(normalizer), normalizer, provider, new("OpenAI", "mock-model"), cache,
            NullLogger<SemanticJobMatcher>.Instance, new SemanticMatchingOptions(BatchSize: 2, RetryTokenLimit: 12000));
    }

    private static JobAnalysis Analysis(int count) => new()
    {
        JobTitle = "Engineer",
        Requirements = [new() { Skill = "C#", Importance = "required" }, .. Enumerable.Range(1, count)
            .Select(index => new JobRequirement
            {
                Skill = $"Maintain production software {index}", Category = "experience",
                OriginalText = "Support and maintain production software services."
            })]
    };

    private static CandidateProfileSnapshot Snapshot() => new(new CandidateProfile
    {
        TechnicalSkills = ["C#"], Experiences = [new()
        {
            Company = "Synthetic Software", Position = "Engineer",
            Responsibilities = ["Maintains production software and supporting services."]
        }]
    }, "test-version");

    private sealed class BatchProvider : IAiModelProvider
    {
        public Func<SemanticRequestInfo, int[], string?> Fail { get; set; } = (_, _) => null;
        public List<(SemanticRequestInfo Info, int[] Ids, int? Limit)> Calls { get; } = [];

        public Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            var info = request.SemanticBatch!;
            using var prompt = JsonDocument.Parse(Assert.IsType<AiUserMessage>(Assert.Single(request.History)).Text);
            var requirements = prompt.RootElement.GetProperty("requirements").EnumerateArray().ToArray();
            var ids = requirements.Select(item => item.GetProperty("requirementId").GetInt32()).ToArray();
            Calls.Add((info, ids, request.MaxOutputTokens));
            var problem = Fail(info, ids);
            if (problem is "max_output_tokens" or "content_filter")
                throw new AiProviderException("Provider ended the semantic request.", diagnosticCode: problem);
            if (problem == "invalid_json")
                return Task.FromResult(new AiAssistantMessage("{", []));

            var rows = requirements.Select(item => new
            {
                requirementId = item.GetProperty("requirementId").GetInt32(),
                classification = "SemanticMatch",
                reason = "The documented work supports the requirement.",
                evidenceIds = new[] { item.GetProperty("allowedEvidenceIds")[0].GetString() }
            }).ToList();
            switch (problem)
            {
                case "missing":
                    rows.RemoveAt(rows.Count - 1);
                    break;
                case "duplicate":
                    rows.Add(rows[^1]);
                    break;
                case "invalid_evidence":
                    rows[^1] = rows[^1] with { evidenceIds = ["not-supplied"] };
                    break;
            }
            return Task.FromResult(new AiAssistantMessage(JsonSerializer.Serialize(new { results = rows }), []));
        }
    }
}
