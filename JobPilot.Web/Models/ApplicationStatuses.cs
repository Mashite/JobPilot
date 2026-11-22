namespace JobPilot.Web.Models;

internal static class ApplicationStatuses
{
    public static IReadOnlyList<string> All { get; } =
        ["Interested", "Applied", "Interviewing", "Offered", "Rejected", "Withdrawn"];
}
