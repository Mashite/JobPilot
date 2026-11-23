using System.Text.Json;
using JobPilot.Web.Models;

namespace JobPilot.Web.Services;

internal sealed record AgentResumeResult(GeneratedResume? Resume, string? Error)
{
    public const string ToolName = "generate_resume";

    public static AgentResumeResult Parse(JsonElement result)
    {
        const string invalidResume = "The resume tool returned a result that could not be opened in the editor.";
        if (result.ValueKind != JsonValueKind.Object)
            return new(null, invalidResume);

        foreach (var property in result.EnumerateObject())
        {
            if (property.Name.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                var detail = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                return new(null, string.IsNullOrWhiteSpace(detail)
                    ? "The resume tool reported an error."
                    : $"The resume tool reported an error. {detail}");
            }
        }

        try
        {
            var resume = result.Deserialize<GeneratedResume>(JsonSerializerOptions.Web);
            if (resume is null || string.IsNullOrWhiteSpace(resume.FullName) ||
                !HasValidItems(resume.TechnicalSkills) || !HasValidItems(resume.Projects) ||
                !HasValidItems(resume.Education) || !HasValidItems(resume.Certifications) || resume.Experiences is null ||
                resume.Experiences.Any(experience => experience is null || !HasValidItems(experience.Highlights)))
            {
                return new(null, invalidResume);
            }

            return new(resume, null);
        }
        catch (JsonException)
        {
            return new(null, invalidResume);
        }
    }

    private static bool HasValidItems(List<string>? items) => items is not null && items.All(item => item is not null);
}
