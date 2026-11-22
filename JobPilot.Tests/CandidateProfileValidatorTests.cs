
using JobPilot.Api.Models;
using JobPilot.Api.Services;
using Xunit;

namespace JobPilot.Tests;

public class CandidateProfileValidatorTests
{
    [Fact]
    public void MissingFullName_ShouldReturnValidationError()
    {
        var profile = new CandidateProfile
        {
            FullName = "",
            ProfessionalTitle = "Senior Software Engineer",
            TechnicalSkills = ["C#", ".NET"],
            Experiences = []
        };

        var validator = new CandidateProfileValidator();

        var errors = validator.Validate(profile);

        Assert.Contains(
            "Full name is required.",
            errors);
    }
}
