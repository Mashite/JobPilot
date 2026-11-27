using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobPilot.Web.Models.Agent;

public sealed record AgentChatRequest(string Message, string? JobDescription = null);

public sealed record AgentChatResponse(
    [property: JsonRequired] string Message,
    [property: JsonRequired] List<AgentToolResult> ExecutedTools);

public sealed record AgentToolResult(
    [property: JsonRequired] string ToolName,
    [property: JsonRequired] JsonElement Result);

public sealed record ChatMessage(string Role, string Content, List<AgentToolResult> Tools, AgentWorkflowView? Workflow = null);

public sealed record AgentWorkflowView(Guid Id, string Stage, string State, string? Error, string? Message, bool CanRetry,
    AgentAnalysisSummary? Analysis, AgentResumeSummary? Resume)
{
    public static string StageLabel(string? stage)
    {
        if (stage?.StartsWith("semantic_matching_", StringComparison.Ordinal) == true)
        {
            var parts = stage.Split('_');
            if (parts.Length is 4 or 5 && int.TryParse(parts[2], out var batch) && int.TryParse(parts[3], out var total))
                return parts.Length == 5 ? $"Retrying semantic matches — Batch {batch} of {total}"
                    : $"Evaluating semantic matches — Batch {batch} of {total}";
        }
        return stage switch
    {
        "intent" => "Understanding the request",
        "loading_profile" => "Loading Master CV",
        "extracting_requirements" => "Analyzing job requirements",
        "matching" => "Matching professional experience",
        "semantic_matching" => "Evaluating semantic matches",
        "finalizing_matching" => "Finalizing matching results",
        "saving_analysis" => "Saving job analysis",
        "generating_resume" => "Generating tailored resume",
        "saving_resume" => "Saving resume draft",
        "final_response" => "Preparing the response",
        "completed" => "Completed",
        _ => "Waiting for the AI provider"
    };
    }
}

public sealed record AgentAnalysisSummary(Guid AnalysisId, int MatchScore, string Status, int MatchedRequirements,
    int MissingRequirements, string Summary, bool IsScoreProvisional = false, int PendingRequirements = 0,
    IReadOnlyList<string>? StrongestAreas = null, IReadOnlyList<string>? GapHighlights = null);

public sealed record AgentResumeSummary(Guid ResumeId, string Status, string Summary, string EditorPath);
