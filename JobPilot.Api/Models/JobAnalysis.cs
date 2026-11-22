
namespace JobPilot.Api.Models;

public class JobAnalysis
{
    public string JobTitle { get; set; } = "";

    public List<JobRequirement> Requirements { get; set; } = [];
}
