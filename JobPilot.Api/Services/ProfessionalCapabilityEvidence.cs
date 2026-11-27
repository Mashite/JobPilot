using System.Text.RegularExpressions;
using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

internal static class ProfessionalCapabilityEvidence
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Negated = new(@"\b(no experience|never|did not|haven't|unfamiliar|learning|plan to)\b", Options);

    public static RequirementMatch? Recognize(RequirementMatch match, IReadOnlyList<SemanticEvidence> evidence)
    {
        if (match.Requirement.MinimumExperienceMonths is > 0)
            return null;
        var skill = match.Requirement.Skill.Trim();
        var professional = Regex.IsMatch(match.Requirement.OriginalText ?? "", @"\b(professional|commercial|enterprise)\b", Options);
        bool Skill(string pattern) => Regex.IsMatch(skill, $"^(?:{pattern})$", Options);
        SemanticEvidence? Fact(string first, string second) => evidence.FirstOrDefault(item =>
            item.Citation.Kind is ("experience" or "responsibility" or "achievement" or "project" or "project achievement" or
                "verified competency" or "professional profile") && !Negated.IsMatch(item.Citation.Excerpt) &&
            (!professional || !item.Citation.Excerpt.Contains("(personal project)", StringComparison.OrdinalIgnoreCase)) &&
            Regex.IsMatch(item.Citation.Excerpt, first, Options) && Regex.IsMatch(item.Citation.Excerpt, second, Options));

        if (Skill(@"(?:relational )?(?:database )?schema design|relational database design|complex (?:relational )?database development"))
        {
            var design = Fact(@"\b(design(?:ed|ing)?|built|created|developed|architected)\b",
                @"\b(?:relational database|database schema|relational data model)s?\b");
            if (design is not null && (!skill.StartsWith("Complex", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(design.Citation.Excerpt, @"\b(from scratch|enterprise|manufacturing|erp|tracking|complex)\b", Options)))
                return Supported("Documented relational database design supports this capability.", design);
        }
        else if (Skill("data integrity"))
        {
            var integrity = Fact(@"\b(constraints|referential integrity|validation rules|validated|enforced)\b",
                @"\b(data integrity|data quality|database records|referential integrity)\b");
            if (integrity is not null)
                return Supported("Documented validation or integrity controls support this requirement.", integrity);
        }
        else if (Skill(@"(?:independent )?(?:technical |software )?architecture (?:leadership|ownership)|independently driv(?:e|ing) (?:technical |software )?architecture"))
        {
            var ownership = Fact(@"\b(independently (?:designed|architected|led)|owned|led|drove|from scratch|sole (?:designer|architect))\b",
                @"\b(architecture|architected)\b");
            if (ownership is not null)
                return Supported("Documented independent architecture work supports technical ownership.", ownership);
        }
        else if (Skill(@"(?:production|operational) troubleshooting|troubleshooting (?:production|operational) (?:issues|systems)"))
        {
            var troubleshooting = Fact(@"\b(troublesh(?:oot(?:ing)?|ot)|diagnos(?:ed|ing)|resolved|debugged|investigated)\b",
                @"\b(production|operational|deployed|factory|manufacturing)\b");
            if (troubleshooting is not null)
                return Supported("Documented troubleshooting of operational or deployed systems supports this capability.", troubleshooting);
        }
        else if (Skill(@"(?:rest(?:ful)? )?(?:web )?apis? development(?: and integration)?"))
        {
            var api = Fact(@"\b(developed|implemented|built|designed|created)\b", @"\b(?:web )?apis?\b");
            var integration = skill.Contains("integration", StringComparison.OrdinalIgnoreCase)
                ? Fact(@"\b(integrat\w*|connect\w*)\b", @"\bapis?\b") : api;
            var rest = skill.StartsWith("REST", StringComparison.OrdinalIgnoreCase)
                ? evidence.FirstOrDefault(item => !Negated.IsMatch(item.Citation.Excerpt) &&
                    Regex.IsMatch(item.Citation.Excerpt, @"\bREST(?:ful)? (?:Web )?APIs?\b", Options)) : api;
            if (api is not null && integration is not null && rest is not null)
                return Supported("Documented API implementation supports the requested API capability.", api, integration, rest);
        }
        return null;

        RequirementMatch Supported(string reason, params SemanticEvidence[] facts) => match with
        {
            Status = "semantic", Reason = reason, CanConfirmSkill = true,
            Citations = facts.Select(fact => fact.Citation).DistinctBy(item => item.Id).ToArray()
        };
    }
}
