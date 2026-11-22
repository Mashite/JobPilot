
using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

public class JobMatchingService(
    SkillNormalizer normalizer)
{
    public JobMatchResult Match(
        JobAnalysis job,
        CandidateProfile profile)
    {
        // Only explicitly verified skills count.
        var candidateSkills = profile.TechnicalSkills
            .Select(normalizer.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Deduplicate aliases from the job requirements.
        // If the same skill appears twice, required wins.
        var requirements = job.Requirements
            .Where(r => !string.IsNullOrWhiteSpace(r.Skill))
            .GroupBy(r => normalizer.Normalize(r.Skill))
            .Select(g => g.OrderByDescending(
                r => r.Importance == "required" ? 3 : 1)
                .First())
            .ToList();

        var matched = new List<string>();
        var unverified = new List<string>();
        var evidence = new List<string>();

        var totalWeight = 0;
        var matchedWeight = 0;

        foreach (var requirement in requirements)
        {
            var skill = normalizer.Normalize(
                requirement.Skill);

            var weight = requirement.Importance == "required"
                ? 3
                : 1;

            totalWeight += weight;

            if (candidateSkills.Contains(skill))
            {
                matchedWeight += weight;
                matched.Add(requirement.Skill);

                evidence.Add(
                    $"{requirement.Skill}: verified in candidate skills");
            }
            else
            {
                unverified.Add(requirement.Skill);
            }
        }

        var score = totalWeight == 0
            ? 0
            : (int)Math.Round(
                100.0 * matchedWeight / totalWeight,
                MidpointRounding.AwayFromZero);

        return new JobMatchResult
        {
            JobTitle = job.JobTitle,
            MatchScore = score,
            MatchingSkills = matched,
            MissingOrUnverifiedSkills = unverified,
            Evidence = evidence
        };
    }
}
