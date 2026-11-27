using System.Text.RegularExpressions;
using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

internal static class JobSuitability
{
    private static readonly (string Name, string Terms)[] Areas =
    [
        ("Database engineering", @"database|sql|schema|data integrity|\borm\b|entity framework|linq|transactions|indexing|concurrency|stored procedures?|\b(triggers|views|ctes)\b|execution plan|query optim|deadlock"),
        ("Software architecture", @"architect|design (principle|pattern)|clean architecture|system modeling"),
        ("Frontend development", @"react|blazor|frontend|front.end|javascript|html|css|angular"),
        ("DevOps and cloud", @"devops|cloud|aws|azure|docker|kubernetes|pipeline|ci/cd|deployment"),
        ("Security", @"security|authorization|authentication|jwt|identity|owasp"),
        ("Technical leadership", @"leader|mentor|ownership|stakeholder|communication|project manag"),
        ("Industry experience", @"manufactur|industrial|factory|mes|andon|erp|domain|logistic|healthcare"),
        ("Core programming and backend", @"program|backend|back.end|software develop|api|integration|java|c#|\.net|troubleshoot")
    ];

    public static string GroupFor(string skill, string category, string? suggested = null) =>
        Areas.FirstOrDefault(area => Regex.IsMatch(skill, area.Terms, RegexOptions.IgnoreCase)).Name ??
        Areas.Select(area => area.Name).FirstOrDefault(name => name.Equals(suggested, StringComparison.OrdinalIgnoreCase)) ??
        (category == "education" ? "Education and credentials" : "Other qualifications");

    public static decimal Credit(RequirementMatch match) => match.Status switch
    {
        "exact" or "equivalent" or "semantic" => 1,
        "partial" => 0.5m,
        "pending_semantic" when match.RetainsPartialCredit => 0.5m,
        _ => 0
    };

    public static IEnumerable<RequirementMatch> ScoringUnits(IEnumerable<RequirementMatch> matches)
    {
        var alternatives = new Dictionary<string, List<RequirementMatch>>(StringComparer.Ordinal);
        foreach (var match in matches)
        {
            if (string.IsNullOrWhiteSpace(match.Requirement.AlternativeGroup))
                yield return match;
            else
            {
                if (!alternatives.TryGetValue(match.Requirement.AlternativeGroup, out var group))
                    alternatives[match.Requirement.AlternativeGroup] = group = [];
                group.Add(match);
            }
        }
        foreach (var group in alternatives.Values)
        {
            var best = group.OrderByDescending(Credit).First();
            var requirement = group.FirstOrDefault(item => item.Requirement.Critical)?.Requirement ??
                group.FirstOrDefault(item => item.Requirement.Importance == "required")?.Requirement ?? best.Requirement;
            if (!ReferenceEquals(requirement, best.Requirement))
                best = best with { Requirement = new JobRequirement
                {
                    Skill = best.Requirement.Skill, Category = best.Requirement.Category, ParentGroup = best.Requirement.ParentGroup,
                    ExpectedProficiency = best.Requirement.ExpectedProficiency, Importance = requirement.Importance, Critical = requirement.Critical
                } };
            yield return best;
        }
    }

    public static JobSuitabilityAssessment Assess(IReadOnlyList<RequirementMatch> matches)
    {
        var units = ScoringUnits(matches).ToArray();
        var areas = units.GroupBy(match => GroupFor(match.Requirement.Skill, match.Requirement.Category, match.Requirement.ParentGroup))
            .Select(group => new CompetencyAreaCoverage(group.Key, Score(group), group.Count()))
            .OrderByDescending(area => area.Coverage).ThenBy(area => area.Name).ToArray();
        var gaps = units.Where(item => item.Status == "missing" && item.Requirement.Importance == "required").ToArray();
        return new(areas.Length == 0 ? 0 : (int)Math.Round(areas.Average(area => area.Coverage), MidpointRounding.AwayFromZero), areas,
            gaps.Where(item => item.Requirement.Critical).Select(item => item.Requirement.Skill).ToArray(),
            gaps.Where(item => !item.Requirement.Critical).Select(item => item.Requirement.Skill).ToArray(),
            units.Where(item => item.Status is "pending_semantic" or "needs_verification" ||
                item.Requirement.ExpectedProficiency is "advanced" or "expert")
                .Select(item => item.Requirement.ExpectedProficiency is "advanced" or "expert"
                    ? $"{item.Requirement.Skill} ({item.Requirement.ExpectedProficiency} proficiency)" : item.Requirement.Skill).ToArray(),
            units.Where(item => Credit(item) == 0.5m).Select(item => item.Requirement.Skill).ToArray());
    }

    public static int Score(IEnumerable<RequirementMatch> units)
    {
        var items = units.ToArray();
        int Weight(RequirementMatch item) => item.Requirement.Importance == "required" ? 3 : 1;
        var total = items.Sum(Weight);
        return total == 0 ? 0 : (int)Math.Round(100 * items.Sum(item => Credit(item) * Weight(item)) / total, MidpointRounding.AwayFromZero);
    }
}
