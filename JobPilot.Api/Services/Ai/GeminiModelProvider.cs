using System.Text;
using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;

namespace JobPilot.Api.Services.Ai;

internal sealed class GeminiModelProvider(Client client, string model) : IAiModelProvider, IAsyncDisposable
{
    public static GeminiModelProvider Create(Func<HttpClient> createClient, string model, string apiKey) =>
        new(new Client(enterprise: false, apiKey: apiKey,
            httpOptions: new HttpOptions
            {
                BaseUrl = "https://generativelanguage.googleapis.com",
                ApiVersion = "v1beta",
                // The HTTP handler owns the retry budget and has access to Retry-After.
                RetryOptions = new HttpRetryOptions { Attempts = 1 }
            },
            clientOptions: new ClientOptions { HttpClientFactory = createClient }), model);

    public async Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var config = new GenerateContentConfig
        {
            AutomaticContinuation = false,
            Temperature = (float?)request.Temperature,
            SystemInstruction = string.IsNullOrWhiteSpace(request.Instructions) ? null : TextContent(request.Instructions),
            MaxOutputTokens = request.MaxOutputTokens
        };
        if (request.OutputSchema is { } schema)
        {
            config.ResponseMimeType = "application/json";
            config.ResponseJsonSchema = schema;
            if (config.MaxOutputTokens is { } tokens)
                config.MaxOutputTokens = Math.Max(tokens, 4096);
        }
        if (request.Tools is { Count: > 0 } tools)
        {
            config.Tools =
            [
                new Tool
                {
                    FunctionDeclarations = tools.Select(tool => new FunctionDeclaration
                    {
                        Name = tool.Name, Description = tool.Description, ParametersJsonSchema = tool.Parameters
                    }).ToList()
                }
            ];
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reply = await client.Models.GenerateContentAsync(model, MapHistory(request.History), config, cancellationToken);
            return MapReply(reply);
        }
        catch (ApiException ex)
        {
            throw ToError(ex.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException("Gemini timed out. Try again.", 504, diagnosticCode: "timeout");
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode is { } status)
                throw ToError((int)status);
            throw new AiProviderException("Could not reach Gemini. Check the connection and try again.",
                diagnosticCode: "connection_failed");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            throw new AiProviderException("Gemini returned a malformed response.", diagnosticCode: "malformed_response");
        }
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();

    private AiProviderException ToError(int status) => status switch
    {
        400 => new("Gemini rejected the request. Check whether the configured model supports this operation.", 502, status, "invalid_request"),
        401 or 403 => new("Gemini denied access. Verify the API key, project permissions and model access.", 502, status, "authentication_or_permission"),
        404 => new($"The configured Gemini model '{model}' was not found or is unavailable to this project.", 502, status, "model_unavailable"),
        429 => new("Gemini quota or rate limit reached. Check the project's quota and try again later.", 429, status, "quota_or_rate_limit"),
        500 or 502 or 503 or 504 => new("Gemini is temporarily unavailable. Try again later.", status == 500 ? 502 : status, status, "temporary_provider_failure"),
        _ => new($"Gemini returned HTTP {status}. Try again.", 502, status, "provider_error")
    };

    private static List<Content> MapHistory(IReadOnlyList<AiMessage> history)
    {
        var contents = new List<Content>();
        for (var index = 0; index < history.Count; index++)
        {
            switch (history[index])
            {
                case AiUserMessage user:
                    contents.Add(TextContent(user.Text, "user"));
                    break;
                case AiAssistantMessage { ProviderContent: Content original }:
                    // Replay the SDK content intact so function-call IDs and thought signatures survive.
                    contents.Add(original);
                    break;
                case AiAssistantMessage assistant:
                    contents.Add(MapAssistant(assistant));
                    break;
                case AiToolResultMessage:
                    var parts = new List<Part>();
                    while (index < history.Count && history[index] is AiToolResultMessage result)
                    {
                        parts.Add(new Part
                        {
                            FunctionResponse = new FunctionResponse
                            {
                                Id = result.Call.Id,
                                Name = result.Call.Name,
                                Response = result.Result.ValueKind == JsonValueKind.Object
                                    ? result.Result.Deserialize<Dictionary<string, object>>()
                                    : new Dictionary<string, object> { ["result"] = result.Result }
                            }
                        });
                        index++;
                    }
                    index--;
                    contents.Add(new Content { Role = "user", Parts = parts });
                    break;
                default:
                    throw new ArgumentException("Unsupported AI message.", nameof(history));
            }
        }

        return contents;
    }

    private static Content TextContent(string text, string? role = null) =>
        new() { Role = role, Parts = [new Part { Text = text }] };

    private static Content MapAssistant(AiAssistantMessage assistant)
    {
        var content = TextContent(assistant.Text, "model");
        var parts = content.Parts!;
        if (assistant.Text.Length == 0)
            parts.Clear();
        foreach (var call in assistant.ToolCalls)
        {
            parts.Add(new Part
            {
                FunctionCall = new FunctionCall
                {
                    Id = call.Id, Name = call.Name, Args = call.Arguments.Deserialize<Dictionary<string, object>>()
                }
            });
        }

        return content;
    }

    private static AiAssistantMessage MapReply(GenerateContentResponse reply)
    {
        if (reply.PromptFeedback?.BlockReason is { } block && block != BlockedReason.BlockedReasonUnspecified)
            throw new AiProviderException("Gemini blocked the request. Try rephrasing it.", diagnosticCode: "blocked_response");

        var candidate = reply.Candidates?.FirstOrDefault();
        var finish = candidate?.FinishReason;
        if (finish is not null && finish != FinishReason.Stop && finish != FinishReason.FinishReasonUnspecified)
        {
            throw new AiProviderException(finish == FinishReason.MaxTokens
                ? "Gemini reached its output limit before completing the response."
                : "Gemini could not complete the response.", diagnosticCode: "incomplete_response");
        }

        if (candidate?.Content is not { Parts: { } parts } content)
            throw new JsonException();

        var text = new StringBuilder();
        var calls = new List<AiToolCall>();
        foreach (var part in parts)
        {
            if (part.Thought != true)
                text.Append(part.Text);
            if (part.FunctionCall is { } function)
            {
                if (string.IsNullOrWhiteSpace(function.Name))
                    throw new JsonException();
                calls.Add(new(function.Name, JsonSerializer.SerializeToElement(function.Args ?? []), function.Id));
            }
        }

        if (text.Length == 0 && calls.Count == 0)
            throw new AiProviderException("Gemini returned no text or tool calls.", diagnosticCode: "empty_response");

        return new(text.ToString(), calls) { ProviderContent = content };
    }
}
