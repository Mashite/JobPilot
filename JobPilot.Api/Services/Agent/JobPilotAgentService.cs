
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using JobPilot.Api.Models.Agent;

namespace JobPilot.Api.Services.Agent;

public sealed class JobPilotAgentService
{
    private readonly HttpClient _ollama;
    private readonly IHttpClientFactory _clients;

    private const string Model = "qwen2.5:3b";
    private const int MaxIterations = 4;

    public JobPilotAgentService(
        HttpClient ollama,
        IHttpClientFactory clients)
    {
        _ollama = ollama;
        _clients = clients;
    }

    public async Task<AgentResponse> RunAsync(
        AgentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            throw new ArgumentException("Message is required.");

        var executedTools = new List<AgentToolExecution>();

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = """
                    You are JobPilot, a job application assistant.

                    Available tools:
                    - analyze_job: Compare a job with the candidate profile.
                    - generate_resume: Generate a tailored resume.

                    Use tools when needed to complete the user's request.
                    If asked to analyze and generate a resume,
                    perform both operations.

                    Never invent candidate experience or qualifications.
                    Never claim to have submitted or saved applications.
                    Do not call the same tool repeatedly.
                    Use the supplied job description.
                    """
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = request.Message +
                    (string.IsNullOrWhiteSpace(request.JobDescription)
                        ? ""
                        : "\n\nJob description:\n" +
                          request.JobDescription)
            }
        };

        var tools = CreateToolDefinitions();

        for (int iteration = 0;
             iteration < MaxIterations;
             iteration++)
        {
            var payload = new JsonObject
            {
                ["model"] = Model,
                ["messages"] = messages.DeepClone(),
                ["tools"] = tools.DeepClone(),
                ["stream"] = false,
                ["options"] = new JsonObject
                {
                    ["temperature"] = 0
                }
            };

            using var response = await _ollama.PostAsJsonAsync(
                "api/chat",
                payload,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync<JsonNode>(
                    cancellationToken: cancellationToken);

            var assistantMessage = result?["message"];

            if (assistantMessage is null)
                throw new InvalidOperationException(
                    "Ollama returned no assistant message.");

            // Preserve the model's tool calls in conversation history.
            messages.Add(assistantMessage.DeepClone());

            var calls = assistantMessage["tool_calls"] as JsonArray;

            if (calls is null || calls.Count == 0)
            {
                var answer =
                    assistantMessage["content"]?.GetValue<string>();

                return new AgentResponse(
                    string.IsNullOrWhiteSpace(answer)
                        ? BuildSummary(executedTools)
                        : answer,
                    executedTools);
            }

            foreach (var call in calls)
            {
                var toolName =
                    call?["function"]?["name"]?.GetValue<string>();

                if (string.IsNullOrWhiteSpace(toolName))
                    throw new InvalidOperationException(
                        "Ollama returned a tool call without a name.");

                JsonElement toolResult;

                if (executedTools.Any(
                    x => x.ToolName == toolName))
                {
                    toolResult = JsonSerializer.SerializeToElement(
                        new { error = "Tool already executed." });
                }
                else if (string.IsNullOrWhiteSpace(
                    request.JobDescription))
                {
                    toolResult = JsonSerializer.SerializeToElement(
                        new { error = "Job description is required." });
                }
                else
                {
                    toolResult = await ExecuteToolAsync(
                        toolName,
                        request.JobDescription,
                        cancellationToken);

                    if (toolName is "analyze_job" or "generate_resume")
                    {
                        executedTools.Add(
                            new AgentToolExecution(
                                toolName,
                                toolResult));
                    }
                }

                // Return tool output to Ollama.
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_name"] = toolName,
                    ["content"] = toolResult.GetRawText()
                });
            }
        }

        return new AgentResponse(
            "The agent reached its execution limit. " +
            BuildSummary(executedTools),
            executedTools);
    }

    private static JsonArray CreateToolDefinitions()
    {
        return new JsonArray
        {
            CreateTool(
                "analyze_job",
                "Compare a job description with the candidate profile."),
            CreateTool(
                "generate_resume",
                "Generate a tailored resume using verified experience.")
        };
    }

    private static JsonObject CreateTool(
        string name,
        string description)
    {
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["jobDescription"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] =
                                "The complete job description."
                        }
                    },
                    ["required"] = new JsonArray
                    {
                        "jobDescription"
                    }
                }
            }
        };
    }

    private async Task<JsonElement> ExecuteToolAsync(
        string toolName,
        string jobDescription,
        CancellationToken cancellationToken)
    {
        var endpoint = toolName switch
        {
            "analyze_job" => "api/jobs/match",
            "generate_resume" => "api/resumes/generate",
            _ => null
        };

        if (endpoint is null)
        {
            return JsonSerializer.SerializeToElement(
                new { error = "Tool not permitted." });
        }

        var client = _clients.CreateClient(
            "JobPilotInternalApi");

        using var response = await client.PostAsJsonAsync(
            endpoint,
            new { jobDescription },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var result = await response.Content
            .ReadFromJsonAsync<JsonDocument>(
                cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                $"Tool {toolName} returned no result.");

        return result.RootElement.Clone();
    }

    private static string BuildSummary(
        List<AgentToolExecution> tools)
    {
        return tools.Count == 0
            ? "No tools were executed."
            : "Completed: " +
              string.Join(", ", tools.Select(x => x.ToolName));
    }
}
