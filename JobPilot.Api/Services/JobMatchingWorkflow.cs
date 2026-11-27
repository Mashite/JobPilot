using System.Text.Json;
using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

internal static class JobMatchingWorkflow
{
    public static async Task<JobMatchResult> MatchAsync(string listing, JobAnalysis extracted, CandidateProfileSnapshot snapshot,
        SemanticJobMatcher semantic, JobMatchReviewStore? store, ILogger logger, CancellationToken cancellationToken,
        Func<SemanticProgress, Task>? semanticStarted = null, JobMatchResult? previous = null,
        Func<JobMatchResult, Task>? saveCheckpoint = null)
    {
        var automatic = await semantic.MatchAsync(extracted, snapshot, cancellationToken, semanticStarted: semanticStarted,
            previous: previous, saveCheckpoint: saveCheckpoint);
        JobReview? review = null;
        if (store is not null)
        {
            try
            {
                review = await store.LoadAsync(listing, automatic.Requirements, cancellationToken);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or JsonException)
            {
                logger.LogWarning("Saved job review could not be loaded. Error type {ErrorType}", ex.GetType().Name);
                automatic.Warnings.Add(JobMatchReviewStore.IsMissingTable(ex)
                    ? "Saved job reviews are unavailable until the AddJobMatchReviews database migration is applied."
                    : "The saved job review could not be loaded. The results shown are from automatic matching.");
            }
        }
        var result = review is null ? automatic : semantic.ApplyReview(automatic, snapshot.Profile, review);
        if (!ReferenceEquals(result, automatic))
        {
            result.SavedReview = review;
            result.AutomaticResult = automatic;
        }
        return result;
    }
}
