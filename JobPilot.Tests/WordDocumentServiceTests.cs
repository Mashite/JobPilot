
using DocumentFormat.OpenXml.Packaging;
using JobPilot.Api.Models.Resume;
using JobPilot.Api.Services;
using Xunit;

namespace JobPilot.Tests;

public class WordDocumentServiceTests
{
    [Fact]
    public void Generate_ShouldCreateValidWordDocument()
    {
        // Arrange
        var resume = new GeneratedResume
        {
            FullName = "Test Candidate",
            ProfessionalTitle = "Software Engineer",
            ProfessionalSummary = "Experienced .NET developer.",
            TechnicalSkills = ["C#", ".NET", "SQL Server"]
        };

        var service = new WordDocumentService();

        // Act
        var bytes = service.Generate(resume);

        // Assert
        Assert.NotEmpty(bytes);

        using var stream = new MemoryStream(bytes);

        using var document = WordprocessingDocument.Open(
            stream,
            false);

        var text = document.MainDocumentPart?
            .Document?
            .Body?
            .InnerText;

        Assert.NotNull(text);
        Assert.Contains("Test Candidate", text);
        Assert.Contains("Software Engineer", text);
        Assert.Contains("SQL Server", text);
    }
}
