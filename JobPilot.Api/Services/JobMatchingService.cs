using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

public class JobMatchingService(SkillNormalizer normalizer)
{
    public JobMatchResult Match(JobAnalysis job, CandidateProfile profile, IEnumerable<string>? confirmedSkills = null,
        IReadOnlyDictionary<string, string>? confirmedEvidence = null, IEnumerable<string>? relatedSkills = null,
        IEnumerable<string>? missingSkills = null)
    {
        var career = (profile.CareerEvidence ?? []).Where(item => item.Verification != EvidenceVerification.Unverified)
            .GroupBy(item => normalizer.Normalize(item.OriginalName))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.ConfirmedExperienceMonths)
                .ThenBy(item => item.ExperienceScope == "personal_project")
                .ThenByDescending(item => item.Category == "Used technologies")
                .ThenByDescending(item => item.Employer is not null).First(), StringComparer.OrdinalIgnoreCase);
        var verifiedSkills = (profile.TechnicalSkills ?? []).Concat(career.Values.Select(item => item.OriginalName))
            .Where(skill => !string.IsNullOrWhiteSpace(skill)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var profileSkills = verifiedSkills.DistinctBy(normalizer.Normalize, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(normalizer.Normalize, StringComparer.OrdinalIgnoreCase);
        var reviewKeys = confirmedSkills?.Select(normalizer.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relatedKeys = relatedSkills?.Select(normalizer.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingKeys = missingSkills?.Select(normalizer.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requirements = GetDistinctRequirements(job);

        var results = requirements.Select(requirement =>
            Compare(requirement, profileSkills, career, reviewKeys, relatedKeys, missingKeys, confirmedEvidence)).ToList();
        var warnings = new List<string>();
        if (profileSkills.Count == 0)
            warnings.Add("No verified technical skills were loaded from Master CV. Add verified skills and analyze the job again.");
        if (requirements.Count == 0)
            warnings.Add("No requirements were extracted from this job description. Check the listing and analyze it again.");

        var result = new JobMatchResult
        {
            JobTitle = job.JobTitle,
            Requirements = requirements,
            ExperienceRequirements = job.ExperienceRequirements ?? [],
            Technologies = job.Technologies ?? [],
            Responsibilities = job.Responsibilities ?? [],
            RequirementMatches = results,
            Warnings = warnings,
            VerifiedSkillCount = verifiedSkills.Count,
            NormalizedVerifiedSkillCount = profileSkills.Count
        };
        Recalculate(result);
        return result;
    }

    internal static void Recalculate(JobMatchResult result)
    {
        var matches = result.RequirementMatches;
        var satisfied = matches.Where(IsScoredMatch).ToList();
        result.MatchScore = JobSuitability.Score(JobSuitability.ScoringUnits(matches));
        result.Suitability = JobSuitability.Assess(matches);
        result.MatchingSkills = satisfied.Select(match => match.Requirement.Skill).ToList();
        result.MissingOrUnverifiedSkills = matches.Where(match => !IsScoredMatch(match)).Select(match => match.Requirement.Skill).ToList();
        result.IsScoreProvisional = matches.Any(match => match.Status == "pending_semantic");
        result.Evidence = matches.Where(match => match.Status is not ("missing" or "needs_verification" or "pending_semantic"))
            .Select(match => $"{match.Requirement.Skill}: {match.Reason}").ToList();
    }

    private static bool IsScoredMatch(RequirementMatch match) => match.Status is "exact" or "equivalent" or "semantic";

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
        Dictionary<string, CareerEvidence> career, HashSet<string>? reviewKeys, HashSet<string>? relatedKeys, HashSet<string>? missingKeys,
        IReadOnlyDictionary<string, string>? confirmedEvidence)
    {
        var key = normalizer.Normalize(requirement.Skill);
        career.TryGetValue(key, out var documented);
        var inProfile = profileSkills.TryGetValue(key, out var profileSkill) &&
            (requirement.Category != "education" || documented is not null);
        var canSave = CanSaveAsProfileSkill(requirement) && !inProfile;
        var needsEvidence = NeedsReviewEvidence(requirement);
        var evidence = confirmedEvidence?.GetValueOrDefault(key);
        if (reviewKeys?.Contains(key) == true && (!needsEvidence || !string.IsNullOrWhiteSpace(evidence)))
        {
            return new(requirement, "exact", profileSkill,
                canSave ? "Confirmed in this review, pending profile save." : "Manually confirmed for this job review.",
                false, canSave, needsEvidence, evidence);
        }
        if (missingKeys?.Contains(key) == true)
            return new(requirement, "missing", null, "Kept as missing in this job review.", true, canSave, needsEvidence);
        if (relatedKeys?.Contains(key) == true && !string.IsNullOrWhiteSpace(evidence))
            return new(requirement, "related", null, "Marked as related in this job review.", true, canSave, needsEvidence, evidence);

        if (requirement.Category != "education" || documented is not null)
        {
            if (inProfile)
            {
                if (documented?.ExperienceScope == "personal_project" &&
                    System.Text.RegularExpressions.Regex.IsMatch(requirement.OriginalText ?? "", @"\b(professional|commercial|enterprise|production)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return new(requirement, "partial", profileSkill, "The verified evidence is from a personal project.",
                        true, false, needsEvidence, EvidenceExcerpt: documented.SourceExcerpt, EvidenceProject: documented.Project);
                if (requirement.MinimumExperienceMonths is > 0 &&
                    !(documented is { Verification: EvidenceVerification.ManuallyConfirmed } &&
                      documented.ConfirmedExperienceMonths >= requirement.MinimumExperienceMonths))
                    return new(requirement, "related", profileSkill, "The skill name is supported, but the minimum experience is unverified.",
                        true, false, needsEvidence, EvidenceExcerpt: documented?.SourceExcerpt,
                        EvidenceEmployer: documented?.Employer, EvidenceProject: documented?.Project);
                var status = string.Equals(normalizer.CleanDisplayName(requirement.Skill),
                    normalizer.CleanDisplayName(profileSkill!), StringComparison.OrdinalIgnoreCase) ? "exact" : "equivalent";
                return new(requirement, status, profileSkill, "Verified in Master CV.", false,
                    EvidenceExcerpt: documented?.SourceExcerpt, EvidenceEmployer: documented?.Employer, EvidenceProject: documented?.Project);
            }

            var related = profileSkills.FirstOrDefault(skill => normalizer.AreRelated(key, skill.Key)).Value;
            if (related is not null)
            {
                var basis = career.GetValueOrDefault(normalizer.Normalize(related));
                return new(requirement, "related", related, "Related technology or practice in Master CV.", true, canSave, needsEvidence,
                    EvidenceExcerpt: basis?.SourceExcerpt, EvidenceEmployer: basis?.Employer, EvidenceProject: basis?.Project);
            }
        }

        var reason = requirement.Category is "education" or "experience"
            ? "This qualification is not verified by the profile's skill entries."
            : "No matching verified skill in Master CV.";
        return new(requirement, "missing", null, reason, true, canSave, needsEvidence);

    }

    private static int Weight(JobRequirement requirement) => requirement.Importance == "required" ? 3 : 1;
}
