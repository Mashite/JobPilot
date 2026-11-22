
using JobPilot.Api.Services;
using Xunit;

namespace JobPilot.Tests;

public class SkillNormalizerTests
{
    private readonly SkillNormalizer _normalizer = new();

    [Theory]
    [InlineData(".NET", ".NET 8/9/10")]
    [InlineData("MES", "Manufacturing Execution Systems")]
    [InlineData("MSSQL", "SQL Server")]
    [InlineData("PLC Communication", "PLC Integration")]
    public void EquivalentSkills_ShouldMatch(
        string first,
        string second)
    {
        // Act
        var result1 = _normalizer.Normalize(first);
        var result2 = _normalizer.Normalize(second);

        // Assert
        Assert.Equal(result1, result2);
    }

    [Fact]
    public void AzureDevOps_ShouldNotMatch_AzureCloud()
    {
        // Act
        var devOps = _normalizer.Normalize("Azure DevOps");
        var cloud = _normalizer.Normalize("Azure Cloud Services");

        // Assert
        Assert.NotEqual(devOps, cloud);
    }
}
