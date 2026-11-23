
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
    [InlineData("C Sharp", "C#")]
    [InlineData("ASP.NET Core", "AspNetCore")]
    [InlineData("EF Core", "Entity Framework Core")]
    [InlineData("CI/CD", "CI/CD pipelines")]
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

    [Theory]
    [InlineData("React", "Blazor")]
    [InlineData("PostgreSQL", "SQL Server")]
    [InlineData("AWS", "Azure")]
    [InlineData(".NET Framework 4.8", ".NET 8")]
    [InlineData("Entity Framework", "Entity Framework Core")]
    public void DistinctTechnologiesNeverNormalizeToTheSameKey(string first, string second) =>
        Assert.NotEqual(_normalizer.Normalize(first), _normalizer.Normalize(second));

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
