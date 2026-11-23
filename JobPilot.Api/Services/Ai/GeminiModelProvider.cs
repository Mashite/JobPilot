using System.Text;
using System.Text.Json;

namespace JobPilot.Api.Services.Ai;

internal sealed class GeminiModelProvider(HttpClient client, string model, string apiKey) : IAiModelProvider, IDisposable
{
    public async Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object> { ["contents"] = MapHistory(request.History) };
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["systemInstruction"] = new { parts = new[] { new { text = request.Instructions } } };

        var generation = new Dictionary<string, object>();
        if (request.Temperature is { } temperature)
            generation["temperature"] = temperature;
        // Gemini's budget includes thinking tokens, so leave room for the structured answer.
        if (request.MaxOutputTokens is { } maxTokens)
            generation["maxOutputTokens"] = Math.Max(4096, maxTokens);
        if (request.OutputSchema is { } schema)
        {
            generation["responseMimeType"] = "application/json";
            generation["responseJsonSchema"] = schema;
        }
        if (generation.Count > 0)
            payload["generationConfig"] = generation;
        if (request.Tools is { Count: > 0 } tools)
        {
            payload["tools"] = new[]
            {
                new { functionDeclarations = tools.Select(tool => new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parametersJsonSchema = tool.Parameters
                }) }
            };
        }

        using var http = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = JsonContent.Create(payload)
        };
        http.Headers.Add("x-goog-api-key", apiKey);
        return await AiProviderHttp.GenerateAsync(client, http, "Gemini", model, ParseReply, cancellationToken);
    }

    public void Dispose() => client.Dispose();

    private static List<object> MapHistory(IReadOnlyList<AiMessage> history)
    {
        var contents = new List<object>();
        for (var index = 0; index < history.Count; index++)
        {
            switch (history[index])
            {
                case AiUserMessage user:
                    contents.Add(new { role = "user", parts = new[] { new { text = user.Text } } });
                    break;
                case AiAssistantMessage { ProviderContent: { } original }:
                    // Replay Gemini's exact model content, including part ordering and thought signatures.
                    contents.Add(original);
                    break;
                case AiAssistantMessage assistant:
                    contents.Add(new { role = "model", parts = MapAssistant(assistant) });
                    break;
                case AiToolResultMessage:
                    var parts = new List<object>();
                    while (index < history.Count && history[index] is AiToolResultMessage result)
                    {
                        parts.Add(MapToolResult(result));
                        index++;
                    }
                    index--;
                    contents.Add(new { role = "user", parts });
                    break;
                default:
                    throw new ArgumentException("Unsupported AI message.", nameof(history));
            }
        }

        return contents;
    }

    private static List<object> MapAssistant(AiAssistantMessage assistant)
    {
        var parts = new List<object>();
        if (assistant.Text.Length > 0)
            parts.Add(new { text = assistant.Text });
        foreach (var call in assistant.ToolCalls)
        {
            var function = new Dictionary<string, object> { ["name"] = call.Name, ["args"] = call.Arguments };
            if (call.Id is { } id)
                function["id"] = id;
            parts.Add(new { functionCall = function });
        }

        return parts;
    }

    private static object MapToolResult(AiToolResultMessage result)
    {
        var function = new Dictionary<string, object>
        {
            ["name"] = result.Call.Name,
            ["response"] = result.Result.ValueKind == JsonValueKind.Object ? result.Result : new { result = result.Result }
        };
        if (result.Call.Id is { } id)
            function["id"] = id;
        return new { functionResponse = function };
    }

    private static AiAssistantMessage ParseReply(JsonElement payload)
    {
        if (payload.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out _))
            throw new AiProviderException("Gemini blocked the request. Try rephrasing it.");

        var candidates = payload.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0)
            throw new JsonException();

        var candidate = candidates[0];
        var finishReason = candidate.TryGetProperty("finishReason", out var finish) ? finish.GetString() : null;
        if (finishReason is not (null or "STOP" or "FINISH_REASON_UNSPECIFIED"))
        {
            throw new AiProviderException(finishReason == "MAX_TOKENS"
                ? "Gemini reached its output limit before completing the response."
                : "Gemini could not complete the response.");
        }

        var content = candidate.GetProperty("content");
        var text = new StringBuilder();
        var calls = new List<AiToolCall>();
        foreach (var part in content.GetProperty("parts").EnumerateArray())
        {
            if (part.TryGetProperty("text", out var partText) &&
                !(part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True))
            {
                text.Append(partText.GetString());
            }

            if (!part.TryGetProperty("functionCall", out var function))
                continue;

            var name = function.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name))
                throw new JsonException();
            var arguments = function.TryGetProperty("args", out var args) ? args.Clone() : JsonSerializer.SerializeToElement(new { });
            if (arguments.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            var id = function.TryGetProperty("id", out var callId) ? callId.GetString() : null;
            calls.Add(new(name, arguments, id));
        }

        if (text.Length == 0 && calls.Count == 0)
            throw new AiProviderException("Gemini returned no text or tool calls.");

        return new(text.ToString(), calls) { ProviderContent = content.Clone() };
    }
}
