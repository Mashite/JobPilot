
using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

public class CandidateProfileValidator
{
    public List<string> Validate(CandidateProfile profile)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.FullName))
            errors.Add("Full name is required.");

        if (string.IsNullOrWhiteSpace(profile.ProfessionalTitle))
            errors.Add("Professional title is required.");

        if (profile.TechnicalSkills is null ||
            profile.TechnicalSkills.Count == 0)
        {
            errors.Add("At least one technical skill is required.");
        }

        if (profile.Experiences is null ||
            profile.Experiences.Count == 0)
        {
            errors.Add("At least one work experience is required.");
        }
        else
        {
            foreach (var experience in profile.Experiences)
            {
                if (string.IsNullOrWhiteSpace(experience.Company))
                    errors.Add("Experience company is required.");

                if (string.IsNullOrWhiteSpace(experience.Position))
                    errors.Add("Experience position is required.");

                if (string.IsNullOrWhiteSpace(experience.StartDate))
                    errors.Add(
                        $"Start date is missing for {experience.Company}.");

                if (string.IsNullOrWhiteSpace(experience.EndDate))
                    errors.Add(
                        $"End date is missing for {experience.Company}. " +
                        "Use 'Present' for current employment.");
            }
        }

        return errors;
    }
}
