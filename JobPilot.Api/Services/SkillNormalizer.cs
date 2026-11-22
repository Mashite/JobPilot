
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

            [".net"] = "dotnet",
            [".net core"] = "dotnet",
            ["dotnet"] = "dotnet",

            ["plc integration"] = "plcintegration",
            ["plc communication"] = "plcintegration",

            ["azure devops"] = "azuredevops",
            ["azure cloud services"] = "azurecloud",
            ["microsoft azure"] = "azurecloud",
            ["azure cloud"] = "azurecloud"
        };


    public string Normalize(string skill)
    {
        if (string.IsNullOrWhiteSpace(skill))
            return string.Empty;

        var value = Regex.Replace(
            skill.Trim().ToLowerInvariant(),
            @"\s+",
            " ");

        // Normalize .NET versions into a general .NET skill
        if (Regex.IsMatch(
            value,
            @"^\.net(?:\s+\d+(?:/\d+)*)?$"))
        {
            return "dotnet";
        }

        return Aliases.TryGetValue(value, out var canonical)
            ? canonical
            : value;
    }

}
