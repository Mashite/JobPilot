
using System.Text.RegularExpressions;

namespace JobPilot.Api.Services;

public class SkillNormalizer
{
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mes"] = "mes",
            ["manufacturing execution systems"] = "mes",
            ["manufacturing execution system"] = "mes",
            ["manufacturing systems"] = "manufacturingsystems",

            ["mssql"] = "sqlserver",
            ["microsoft sql server"] = "sqlserver",
            ["sql server"] = "sqlserver",

            ["c#"] = "csharp",
            ["c sharp"] = "csharp",
            ["c-sharp"] = "csharp",
            ["asp.net core"] = "aspnetcore",
            ["aspnet core"] = "aspnetcore",
            ["aspnetcore"] = "aspnetcore",
            ["asp net core"] = "aspnetcore",

            ["entity framework"] = "entityframework",
            ["ef"] = "entityframework",
            ["entity framework core"] = "entityframeworkcore",
            ["ef core"] = "entityframeworkcore",
            ["efcore"] = "entityframeworkcore",

            [".net"] = "dotnet",
            [".net core"] = "dotnet",
            [".net / .net core"] = "dotnet",
            ["dotnet"] = "dotnet",
            [".net framework"] = "dotnetframework",
            ["dotnet framework"] = "dotnetframework",

            ["react.js"] = "react",
            ["reactjs"] = "react",
            ["postgres"] = "postgresql",
            ["amazon web services"] = "aws",
            ["ci/cd"] = "ci/cd",
            ["ci / cd"] = "ci/cd",
            ["ci cd"] = "ci/cd",
            ["ci/cd pipeline"] = "ci/cd",
            ["ci/cd pipelines"] = "ci/cd",
            ["continuous integration and continuous delivery"] = "ci/cd",
            ["continuous integration and continuous deployment"] = "ci/cd",
            ["devops practices"] = "devops",
            ["application architecture"] = "software architecture",
            ["software design principles"] = "software design principles",

            ["plc integration"] = "plcintegration",
            ["plc communication"] = "plcintegration",
            ["wcf services"] = "wcf",

            ["azure devops"] = "azuredevops",
            ["azure cloud services"] = "azurecloud",
            ["microsoft azure"] = "azurecloud",
            ["azure cloud"] = "azurecloud"
        };

    private static readonly Regex NamedTechnologies = new(
        @"(?<![\w.#])(?:ASP\s*\.?\s*NET\s*Core|(?:\.NET|dotnet)\s+Framework(?:\s+[1-4](?:\.\d+)*)?|(?:\.NET|dotnet)(?:\s+Core)?(?:\s+\d+(?:[./]\d+)*)?|Entity\s+Framework(?:\s+Core)?|EF\s*Core|Microsoft\s+SQL\s+Server|SQL\s+Server|MSSQL|C#|C[ -]Sharp|Azure\s+DevOps|DevOps(?:\s+practices)?|CI\s*/\s*CD(?:\s+pipelines?)?|Amazon\s+Web\s+Services|AWS|PostgreSQL|Postgres|React(?:\.js|JS)?|Context\s+API|Blazor|Docker|Kubernetes|TypeScript|JavaScript|Python|Angular)(?![\w#])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);


    public string Normalize(string skill)
    {
        if (string.IsNullOrWhiteSpace(skill))
            return string.Empty;

        var value = CleanDisplayName(skill).ToLowerInvariant();

        if (Regex.IsMatch(value, @"^(?:\.net|dotnet) (?:framework(?: [1-4](?:\.\d+)*)?|[1-4](?:\.\d+)*)$"))
            return "dotnetframework";
        if (Regex.IsMatch(value, @"^(?:\.net|dotnet)(?: core(?: \d+(?:\.\d+)*)?| (?:[5-9]|[1-9]\d+)(?:[./]\d+)*)$"))
            return "dotnet";

        return Aliases.TryGetValue(value, out var canonical)
            ? canonical
            : value;
    }

    internal IEnumerable<string> NamesFor(string skill)
    {
        yield return skill;
        var canonical = Normalize(skill);
        foreach (var alias in Aliases.Where(item => item.Value.Equals(canonical, StringComparison.OrdinalIgnoreCase)))
            yield return alias.Key;
    }

    // Related skills supply context for review. Equivalence and score use Normalize only.
    public bool AreRelated(string requirementKey, string profileKey) => (requirementKey, profileKey) switch
    {
        ("dotnet", "dotnetframework") or ("dotnetframework", "dotnet") => true,
        ("entityframework", "entityframeworkcore") or ("entityframeworkcore", "entityframework") => true,
        ("devops", "azuredevops") or ("azuredevops", "devops") => true,
        ("software design patterns", "software architecture") or ("software design principles", "software architecture") => true,
        _ => false
    };

    internal IReadOnlyList<string> SplitNamedTechnologies(string text)
    {
        var matches = NamedTechnologies.Matches(text);
        if (matches.Count == 1 && Regex.IsMatch(text, @"\b(?:proficiency|expertise|experience|knowledge|familiarity)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return [matches[0].Value];

        return matches.Count > 1
            ? matches.Select(match => match.Value).DistinctBy(Normalize, StringComparer.OrdinalIgnoreCase).ToArray()
            : [CleanDisplayName(text)];
    }

    public string CleanDisplayName(string skill) => Regex.Replace(skill.Trim(), @"\s+", " ");
}
