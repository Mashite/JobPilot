using System.Text.Json;

namespace JobPilot.Api.Services.Ai;

internal sealed class OllamaModelProvider(HttpClient client, string model) : IAiModelProvider, IDisposable
{
    public async Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            messages.Add(new { role = "system", content = request.Instructions });
        messages.AddRange(request.History.Select(MapMessage));

        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false
        };
        var options = new Dictionary<string, object>();
        if (request.Temperature is { } temperature)
            options["temperature"] = temperature;
        if (request.MaxOutputTokens is { } maxTokens)
            options["num_predict"] = maxTokens;
        if (options.Count > 0)
            payload["options"] = options;
        if (request.OutputSchema is { } schema)
            payload["format"] = schema;
        if (request.Tools is { Count: > 0 } tools)
        {
            payload["tools"] = tools.Select(tool => new
            {
                type = "function",
                function = new { name = tool.Name, description = tool.Description, parameters = tool.Parameters }
            });
        }

        using var http = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = JsonContent.Create(payload) };
        return await AiProviderHttp.GenerateAsync(client, http, "Ollama", model, ParseReply, cancellationToken);
    }

    public void Dispose() => client.Dispose();

    private static object MapMessage(AiMessage message) => message switch
    {
        AiUserMessage user => new { role = "user", content = user.Text },
        AiAssistantMessage { ProviderContent: { } content } => content,
        AiAssistantMessage assistant => new
        {
            role = "assistant",
            content = assistant.Text,
            tool_calls = assistant.ToolCalls.Select(call => new { function = new { name = call.Name, arguments = call.Arguments } })
        },
        AiToolResultMessage tool => new { role = "tool", tool_name = tool.Call.Name, content = tool.Result.GetRawText() },
        _ => throw new ArgumentException("Unsupported AI message.", nameof(message))
    };

    private static AiAssistantMessage ParseReply(JsonElement payload)
    {
        var message = payload.GetProperty("message");
        var text = message.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";
        var calls = new List<AiToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind != JsonValueKind.Null)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var name = function.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(name))
                    throw new JsonException();

                var arguments = function.TryGetProperty("arguments", out var args) ? args : JsonSerializer.SerializeToElement(new { });
                if (arguments.ValueKind != JsonValueKind.Object)
                    throw new JsonException();

                calls.Add(new(name, arguments.Clone()));
            }
        }

        if (!message.TryGetProperty("content", out _) && calls.Count == 0)
            throw new JsonException();

        return new(text, calls) { ProviderContent = message.Clone() };
    }
}
