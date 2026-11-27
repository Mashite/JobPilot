namespace JobPilot.Web.Models;

internal static class JobMatchPresentation
{
    public static string Label(string? status) => status switch
    {
        "exact" => "Exact Match",
        "equivalent" => "Equivalent Match",
        "semantic" => "AI Semantic Match",
        "related" or "partial" => "Partial Match",
        "missing" => "Missing",
        "needs_verification" => "Needs Verification",
        "pending_semantic" => "Semantic Evaluation Pending",
        _ => "Unreviewed"
    };

    public static string Group(string? status) => status switch
    {
        "related" => "partial",
        null => "missing",
        _ => status
    };
}
