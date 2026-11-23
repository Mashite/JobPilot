using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenAI.Responses;

namespace JobPilot.Api.Services.Ai;

internal sealed record OpenAiProviderSettings(string Model, int MaxOutputTokens, TimeSpan RequestTimeout);

// The official SDK marks its Responses client as experimental.
#pragma warning disable OPENAI001
internal sealed class OpenAiModelProvider(
    HttpClient httpClient, string apiKey, OpenAiProviderSettings settings, ILogger<OpenAiModelProvider> logger)
    : IAiModelProvider, IDisposable
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);
    private readonly ResponsesClient client = new(new ApiKeyCredential(apiKey), new ResponsesClientOptions
    {
        Endpoint = new Uri("https://api.openai.com/v1"),
        Transport = new HttpClientPipelineTransport(httpClient),
        NetworkTimeout = settings.RequestTimeout,
        RetryPolicy = new ClientRetryPolicy(0),
        ClientLoggingOptions = new ClientLoggingOptions { EnableLogging = false, EnableMessageContentLogging = false }
    });

    public async Task<AiAssistantMessage> GenerateAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(settings.RequestTimeout);
        var payload = CreatePayload(request);
        try
        {
            return await SendAsync(payload, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException("The OpenAI request timed out. Try again.", 504, diagnosticCode: "timeout");
        }
    }

    private async Task<AiAssistantMessage> SendAsync(BinaryData payload, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            string outcome = "failed";
            int? upstreamStatus = null;
            int? inputTokens = null, outputTokens = null, totalTokens = null;
            TimeSpan delay;
            try
            {
                using var body = BinaryContent.Create(payload);
                var result = await client.CreateResponseAsync(body, new RequestOptions
                {
                    CancellationToken = cancellationToken,
                    ErrorOptions = ClientErrorBehaviors.NoThrow
                });
                using var response = result.GetRawResponse();
                upstreamStatus = response.Status;
                if (response.Status is >= 200 and < 300)
                {
                    using var json = JsonDocument.Parse(response.Content.ToMemory());
                    if (json.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        inputTokens = ReadTokenCount(usage, "input_tokens");
                        outputTokens = ReadTokenCount(usage, "output_tokens");
                        totalTokens = ReadTokenCount(usage, "total_tokens");
                    }

                    var reply = MapReply(json.RootElement);
                    outcome = "completed";
                    return reply;
                }

                var errorCode = ReadErrorCode(response.Content);
                var error = ToError(response.Status, errorCode);
                outcome = error.DiagnosticCode!;
                if (attempt >= MaxAttempts || !IsTransient(response.Status, errorCode))
                    throw error;

                delay = GetRetryDelay(response.Headers, attempt);
                if (delay > MaxRetryAfter)
                    throw error;
            }
            catch (OperationCanceledException)
            {
                outcome = cancellationToken.IsCancellationRequested ? "canceled_or_deadline" : "timeout";
                throw;
            }
            catch (ClientResultException ex) when (ex.Status == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcome = "connection_failed";
                throw new AiProviderException("Could not reach OpenAI. Check the connection.", diagnosticCode: outcome);
            }
            catch (HttpRequestException)
            {
                outcome = "connection_failed";
                throw new AiProviderException("Could not reach OpenAI. Check the connection.", diagnosticCode: outcome);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                outcome = "malformed_response";
                throw new AiProviderException("OpenAI returned a malformed response.", diagnosticCode: outcome);
            }
            catch (AiProviderException ex)
            {
                outcome = ex.DiagnosticCode ?? outcome;
                throw;
            }
            finally
            {
                logger.LogInformation(
                    "OpenAI request {Model} attempt {Attempt} outcome {Outcome} upstream HTTP {UpstreamStatus} elapsed {ElapsedMs} ms input tokens {InputTokens} output tokens {OutputTokens} total tokens {TotalTokens}",
                    settings.Model, attempt, outcome, upstreamStatus, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    inputTokens, outputTokens, totalTokens);
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    private BinaryData CreatePayload(AiRequest request)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = settings.Model,
            ["store"] = false,
            ["input"] = MapHistory(request.History),
            ["max_output_tokens"] = Math.Min(settings.MaxOutputTokens, request.MaxOutputTokens ?? settings.MaxOutputTokens)
        };
        if (settings.Model == "gpt-6-luna")
            payload["reasoning"] = new { effort = "none" };
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["instructions"] = request.Instructions;
        if (request.OutputSchema is { } schema)
        {
            payload["text"] = new { format = new { type = "json_schema", name = "jobpilot_output", strict = true, schema } };
        }
        if (request.Tools is { Count: > 0 } tools)
        {
            payload["include"] = new[] { "reasoning.encrypted_content" };
            payload["tools"] = tools.Select(tool => new
            {
                type = "function", name = tool.Name, description = tool.Description, parameters = tool.Parameters, strict = true
            });
        }
        return BinaryData.FromObjectAsJson(payload);
    }

    private static IEnumerable<object> MapHistory(IEnumerable<AiMessage> history)
    {
        foreach (var message in history)
        {
            switch (message)
            {
                case AiUserMessage user:
                    yield return new { role = "user", content = user.Text };
                    break;
                case AiAssistantMessage { ProviderContent: JsonElement output }:
                    foreach (var item in output.EnumerateArray())
                        yield return item;
                    break;
                case AiAssistantMessage assistant:
                    if (assistant.Text.Length > 0)
                        yield return new { role = "assistant", content = assistant.Text };
                    foreach (var call in assistant.ToolCalls)
                        yield return new { type = "function_call", call_id = call.Id, name = call.Name, arguments = call.Arguments.GetRawText() };
                    break;
                case AiToolResultMessage tool:
                    yield return new { type = "function_call_output", call_id = tool.Call.Id, output = tool.Result.GetRawText() };
                    break;
            }
        }
    }

    private static AiAssistantMessage MapReply(JsonElement response)
    {
        if (ReadString(response, "status") is not "completed")
            throw new AiProviderException("OpenAI did not complete the response. Check the output token limit or try again.",
                diagnosticCode: "incomplete_response");

        var output = response.GetProperty("output");
        var text = new StringBuilder();
        var calls = new List<AiToolCall>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in output.EnumerateArray())
        {
            switch (ReadString(item, "type"))
            {
                case "message":
                    foreach (var part in item.GetProperty("content").EnumerateArray())
                    {
                        if (ReadString(part, "type") == "refusal")
                            throw new AiProviderException("OpenAI declined the request.", diagnosticCode: "refusal");
                        if (ReadString(part, "type") == "output_text")
                            text.Append(ReadString(part, "text"));
                    }
                    break;
                case "function_call":
                    var id = ReadString(item, "call_id");
                    var name = ReadString(item, "name");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !callIds.Add(id))
                        throw new JsonException();
                    calls.Add(new AiToolCall(name, ParseArguments(ReadString(item, "arguments")), id));
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(text.ToString()) && calls.Count == 0)
            throw new AiProviderException("OpenAI returned no text or tool calls.", diagnosticCode: "empty_response");
        return new AiAssistantMessage(text.ToString(), calls) { ProviderContent = output.Clone() };
    }

    private static JsonElement ParseArguments(string? json)
    {
        if (json is null)
            return default;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Return the tool validation error to OpenAI without executing the call.
            return default;
        }
    }

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadTokenCount(JsonElement usage, string property) =>
        usage.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count)
            ? count : null;

    private static string? ReadErrorCode(BinaryData data)
    {
        try
        {
            using var document = JsonDocument.Parse(data.ToMemory());
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                ? ReadString(error, "code") ?? ReadString(error, "type") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan GetRetryDelay(PipelineResponseHeaders headers, int attempt)
    {
        if (headers.TryGetValue("Retry-After", out var retryAfter) &&
            RetryConditionHeaderValue.TryParse(retryAfter, out var parsed))
        {
            var remaining = parsed.Delta ?? (parsed.Date!.Value - DateTimeOffset.UtcNow);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return TimeSpan.FromMilliseconds(250 * (1 << (attempt - 1)));
    }

    private static bool IsTransient(int status, string? code) =>
        status is 500 or 502 or 503 or 504 || (status == 429 && code != "insufficient_quota");

    private AiProviderException ToError(int status, string? code) => status switch
    {
        400 => new("OpenAI rejected the request. Check the configured model and request settings.", 502, status, "invalid_request"),
        401 => new("OpenAI rejected the API key. Check JobPilot.Api User Secrets.", 502, status, "invalid_api_key"),
        403 => new($"This API project does not have permission to use '{settings.Model}'. Check project or model access.", 502, status, "model_access_denied"),
        404 => new($"OpenAI could not find '{settings.Model}' or this API project cannot access it.", 502, status, "model_not_found"),
        429 when code == "insufficient_quota" => new("The OpenAI API project has insufficient quota. Check its credits and limits.", 429, status, "insufficient_quota"),
        429 => new("OpenAI rate limit reached. Try again later.", 429, status, "rate_limit"),
        500 or 502 or 503 or 504 => new("OpenAI returned a temporary service error. Try again later.", status == 500 ? 502 : status, status, "temporary_provider_failure"),
        _ => new($"OpenAI returned HTTP {status}.", 502, status, "provider_error")
    };

    public void Dispose() => httpClient.Dispose();
}
#pragma warning restore OPENAI001
