using System.Text.Json;
using JobPilot.Api.Data;
using JobPilot.Api.Data.Entities;
using JobPilot.Api.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace JobPilot.Api.Services;

internal sealed class JobMatchReviewStore(JobPilotDbContext database, SkillNormalizer normalizer)
{
    internal static bool IsMissingTable(Exception error) =>
        error is SqlException { Number: 208 } or DbUpdateException { InnerException: SqlException { Number: 208 } };

    public async Task<JobReview?> LoadAsync(string listing, IReadOnlyList<JobRequirement> requirements, CancellationToken cancellationToken)
    {
        var key = JobRequirementCache.Key(listing.Trim());
        var record = await database.JobMatchReviews.AsNoTracking().SingleOrDefaultAsync(row => row.ListingKey == key, cancellationToken);
        if (record is null)
            return null;
        var stored = JsonSerializer.Deserialize<Dictionary<string, Decision>>(record.ReviewJson, JsonSerializerOptions.Web) ?? [];
        var active = requirements.Where(requirement => stored.ContainsKey(RequirementKey(requirement)))
            .Select(requirement => (requirement.Skill, Decision: stored[RequirementKey(requirement)])).ToArray();
        return new JobReview(
            active.Where(item => item.Decision.Status == "confirmed").Select(item => item.Skill).ToArray(),
            active.Where(item => item.Decision.Status == "related").Select(item => item.Skill).ToArray(),
            active.Where(item => item.Decision.Status == "missing").Select(item => item.Skill).ToArray(),
            active.Where(item => item.Decision.Evidence is not null)
                .ToDictionary(item => item.Skill, item => item.Decision.Evidence!, StringComparer.OrdinalIgnoreCase));
    }

    public async Task SaveAsync(string listing, IReadOnlyList<JobRequirement> requirements, JobReview review,
        CancellationToken cancellationToken)
    {
        var active = requirements.ToDictionary(item => normalizer.Normalize(item.Skill));
        var notes = review.Evidence.ToDictionary(item => normalizer.Normalize(item.Key), item => item.Value);
        var decisions = new Dictionary<string, Decision>();
        foreach (var (skills, status) in new[] { (review.Confirmed, "confirmed"), (review.Related, "related"), (review.Missing, "missing") })
        {
            foreach (var skill in skills)
            {
                var key = normalizer.Normalize(skill);
                if (active.TryGetValue(key, out var requirement))
                    decisions[RequirementKey(requirement)] = new(status, notes.GetValueOrDefault(key));
            }
        }
        var listingKey = JobRequirementCache.Key(listing.Trim());
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var query = database.Database.IsSqlServer()
            ? database.JobMatchReviews.FromSqlRaw("SELECT * FROM [JobMatchReviews] WITH (UPDLOCK, HOLDLOCK)")
            : database.JobMatchReviews;
        var row = await query.SingleOrDefaultAsync(item => item.ListingKey == listingKey, cancellationToken);
        if (decisions.Count == 0)
        {
            if (row is not null)
                database.JobMatchReviews.Remove(row);
        }
        else
        {
            if (row is null)
            {
                row = new JobMatchReviewRecord { ListingKey = listingKey };
                database.JobMatchReviews.Add(row);
            }
            row.ReviewJson = JsonSerializer.Serialize(decisions, JsonSerializerOptions.Web);
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private string RequirementKey(JobRequirement requirement) => JobRequirementCache.Key(JsonSerializer.Serialize(new
    {
        Skill = normalizer.Normalize(requirement.Skill), requirement.Category, requirement.MinimumExperienceMonths, requirement.OriginalText
    }));

    private sealed record Decision(string Status, string? Evidence);
}
