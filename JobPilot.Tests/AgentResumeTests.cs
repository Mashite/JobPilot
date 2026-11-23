using System.Text.Json;
using JobPilot.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace JobPilot.Tests;

public class AgentResumeTests
{
    private const string ResumeJson = """
        {
          "fullName": "Test Candidate",
          "professionalTitle": "Developer",
          "professionalSummary": "Builds .NET applications.",
          "technicalSkills": ["C#"],
          "experiences": [{
            "company": "Example Company",
            "position": "Engineer",
            "startDate": "2020",
            "endDate": "2024",
            "highlights": ["Built APIs."]
          }],
          "projects": ["Job tracker"],
          "education": ["Computer science"]
        }
        """;

    [Fact]
    public void ParseReadsTheUnwrappedResumeToolPayload()
    {
        using var json = JsonDocument.Parse(ResumeJson);

        var result = AgentResumeResult.Parse(json.RootElement);

        Assert.Null(result.Error);
        var resume = Assert.IsType<Web.Models.GeneratedResume>(result.Resume);
        Assert.Equal("Test Candidate", resume.FullName);
        Assert.Equal("Developer", resume.ProfessionalTitle);
        Assert.Equal("Builds .NET applications.", resume.ProfessionalSummary);
        Assert.Equal("C#", Assert.Single(resume.TechnicalSkills));
        Assert.Equal("Job tracker", Assert.Single(resume.Projects));
        Assert.Equal("Computer science", Assert.Single(resume.Education));
        var experience = Assert.Single(resume.Experiences);
        Assert.Equal("Example Company", experience.Company);
        Assert.Equal("Engineer", experience.Position);
        Assert.Equal("2020", experience.StartDate);
        Assert.Equal("2024", experience.EndDate);
        Assert.Equal("Built APIs.", Assert.Single(experience.Highlights));
    }

    [Theory]
    [InlineData("""{"error":"Generation failed","fullName":"Test Candidate"}""", "Generation failed")]
    [InlineData("""{"Error":null,"fullName":"Test Candidate"}""", "reported an error")]
    [InlineData("""{"fullName":" "}""", "could not be opened")]
    [InlineData("""{"fullName":"Test Candidate","experiences":[null]}""", "could not be opened")]
    [InlineData("""{"fullName":"Test Candidate","experiences":[{"highlights":null}]}""", "could not be opened")]
    [InlineData("""{"fullName":"Test Candidate","technicalSkills":"C#"}""", "could not be opened")]
    [InlineData("null", "could not be opened")]
    public void ParseRejectsToolErrorsAndInvalidResumes(string payload, string expectedError)
    {
        using var json = JsonDocument.Parse(payload);

        var result = AgentResumeResult.Parse(json.RootElement);

        Assert.Null(result.Resume);
        Assert.Contains(expectedError, result.Error);
    }

    [Fact]
    public void DraftTransferKeepsAllFieldsAndIsolatesEditsAndScopes()
    {
        using var json = JsonDocument.Parse(ResumeJson);
        var resume = Assert.IsType<Web.Models.GeneratedResume>(AgentResumeResult.Parse(json.RootElement).Resume);
        using var provider = new ServiceCollection().AddScoped<AgentResumeDraftState>().BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var chatState = firstScope.ServiceProvider.GetRequiredService<AgentResumeDraftState>();

        chatState.Open(resume, "Build .NET applications.");

        var editorState = firstScope.ServiceProvider.GetRequiredService<AgentResumeDraftState>();
        var draft = Assert.IsType<AgentResumeDraft>(editorState.Current);
        Assert.Equal("Build .NET applications.", draft.JobDescription);
        Assert.Null(draft.ApplicationId);
        Assert.Equal(JsonSerializer.Serialize(resume), JsonSerializer.Serialize(draft.Resume));
        draft.Resume.Experiences[0].Highlights[0] = "Edited highlight.";
        Assert.Equal("Built APIs.", resume.Experiences[0].Highlights[0]);
        Assert.Null(secondScope.ServiceProvider.GetRequiredService<AgentResumeDraftState>().Current);
    }
}
