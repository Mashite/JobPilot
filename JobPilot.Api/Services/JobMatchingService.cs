using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

public class JobMatchingService(SkillNormalizer normalizer)
{
    public JobMatchResult Match(JobAnalysis job, CandidateProfile profile, IEnumerable<string>? confirmedSkills = null,
        IReadOnlyDictionary<string, string>? confirmedEvidence = null)
    {
        var verifiedSkills = (profile.TechnicalSkills ?? []).Where(skill => !string.IsNullOrWhiteSpace(skill)).ToList();
        var profileSkills = verifiedSkills.DistinctBy(normalizer.Normalize, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(normalizer.Normalize, StringComparer.OrdinalIgnoreCase);
        var reviewKeys = confirmedSkills?.Select(normalizer.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requirements = GetDistinctRequirements(job);

        var results = requirements.Select(requirement => Compare(requirement, profileSkills, reviewKeys, confirmedEvidence)).ToList();
        var exact = results.Where(match => match.Status == "exact").ToList();
        var totalWeight = requirements.Sum(Weight);
        var matchedWeight = exact.Sum(match => Weight(match.Requirement));
        var score = totalWeight == 0 ? 0 : (int)Math.Round(100.0 * matchedWeight / totalWeight, MidpointRounding.AwayFromZero);

        var warnings = new List<string>();
        if (profileSkills.Count == 0)
            warnings.Add("No verified technical skills were loaded from Master CV. Add verified skills and analyze the job again.");
        if (requirements.Count == 0)
            warnings.Add("No requirements were extracted from this job description. Check the listing and analyze it again.");

        return new JobMatchResult
        {
            JobTitle = job.JobTitle,
            MatchScore = score,
            MatchingSkills = exact.Select(match => match.Requirement.Skill).ToList(),
            MissingOrUnverifiedSkills = results.Where(match => match.Status != "exact").Select(match => match.Requirement.Skill).ToList(),
            Evidence = results.Where(match => match.Status != "missing")
                .Select(match => $"{match.Requirement.Skill}: {match.Reason}").ToList(),
            Requirements = requirements,
            ExperienceRequirements = job.ExperienceRequirements ?? [],
            Technologies = job.Technologies ?? [],
            Responsibilities = job.Responsibilities ?? [],
            RequirementMatches = results,
            Warnings = warnings,
            VerifiedSkillCount = verifiedSkills.Count,
            NormalizedVerifiedSkillCount = profileSkills.Count
        };
    }

    // Required wins over preferred for aliases of the same skill.
    internal List<JobRequirement> GetDistinctRequirements(JobAnalysis job) =>
        job.Requirements.Where(requirement => !string.IsNullOrWhiteSpace(requirement.Skill))
            .GroupBy(requirement => normalizer.Normalize(requirement.Skill))
            .Select(group => group.OrderByDescending(Weight)
                .ThenByDescending(requirement => requirement.MinimumExperienceMonths)
                .First())
            .ToList();

    internal static bool CanSaveAsProfileSkill(JobRequirement requirement) =>
        requirement.Category is ("technology" or "domain" or "soft_skill") &&
        CandidateProfileService.IsValidSkill(requirement.Skill);

    internal static bool NeedsReviewEvidence(JobRequirement requirement) =>
        !CanSaveAsProfileSkill(requirement) || requirement.MinimumExperienceMonths is > 0;

    private RequirementMatch Compare(JobRequirement requirement, Dictionary<string, string> profileSkills,
        HashSet<string>? reviewKeys, IReadOnlyDictionary<string, string>? confirmedEvidence)
    {
        var key = normalizer.Normalize(requirement.Skill);
        var inProfile = profileSkills.TryGetValue(key, out var profileSkill);
        var canSave = CanSaveAsProfileSkill(requirement) && !inProfile;
        var needsEvidence = NeedsReviewEvidence(requirement);
        var evidence = confirmedEvidence?.GetValueOrDefault(key);
        if (reviewKeys?.Contains(key) == true && (!needsEvidence || !string.IsNullOrWhiteSpace(evidence)))
        {
            return new(requirement, "exact", profileSkill,
                canSave ? "Confirmed in this review, pending profile save." : "Manually confirmed for this job review.",
                false, canSave, needsEvidence, evidence);
        }

        if (requirement.Category != "education")
        {
            if (inProfile)
            {
                if (requirement.MinimumExperienceMonths is > 0)
                    return new(requirement, "related", profileSkill, "The skill name is supported, but the minimum experience is unverified.",
                        true, false, needsEvidence);
                return new(requirement, "exact", profileSkill, "Verified in Master CV.", false);
            }

            var related = profileSkills.FirstOrDefault(skill => normalizer.AreRelated(key, skill.Key)).Value;
            if (related is not null)
                return new(requirement, "related", related, "Related technology or practice in Master CV.", true, canSave, needsEvidence);
        }

        var reason = requirement.Category is "education" or "experience"
            ? "This qualification is not verified by the profile's skill entries."
            : "No matching verified skill in Master CV.";
        return new(requirement, "missing", null, reason, true, canSave, needsEvidence);
    }

    private static int Weight(JobRequirement requirement) => requirement.Importance == "required" ? 3 : 1;
}
