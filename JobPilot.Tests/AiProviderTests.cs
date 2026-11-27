using System.Net;
using System.Text;
using System.Text.Json;
using JobPilot.Api.Models.Agent;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Agent;
using JobPilot.Api.Services.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JobPilot.Tests;

public sealed class AiProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothProvidersGenerateTextAndSendStructuredSchemas(bool gemini)
    {
        var analysisJson = """{"jobTitle":"Developer","requirements":[{"skill":"C#","importance":"required"}]}""";
        var handler = new FakeHttp(
            JsonReply(gemini, "Extracted requirements."), JsonReply(gemini, analysisJson));
        using var client = CreateClient(handler);
        var service = new JobAnalysisService(CreateProvider(gemini, client), new SkillNormalizer());

        Assert.Equal("Extracted requirements.", await service.AnalyzeJobAsync("A job listing."));
        var analysis = await service.ExtractRequirementsAsync("A job listing.");
        Assert.Equal("Developer", analysis.JobTitle);
        Assert.Equal("C#", Assert.Single(analysis.Requirements).Skill);
        var wire = handler.Requests[1];
        if (gemini)
        {
            Assert.Equal("/v1beta/models/test-model:generateContent", wire.Path);
            Assert.Equal("test-key", wire.ApiKey);
            var config = wire.Body.GetProperty("generationConfig");
            Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
            Assert.Equal("object", config.GetProperty("responseJsonSchema").GetProperty("type").GetString());
            Assert.True(wire.Body.GetProperty("systemInstruction").TryGetProperty("parts", out _));
        }
        else
        {
            Assert.Equal("/api/chat", wire.Path);
            Assert.Null(wire.ApiKey);
            Assert.Equal("test-model", wire.Body.GetProperty("model").GetString());
            Assert.Equal("object", wire.Body.GetProperty("format").GetProperty("type").GetString());
        }
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("""{"jobTitle":"Developer","requirements":[null]}""")]
    public async Task ExtractionRejectsMalformedStructuredOutput(string content)
    {
        using var client = CreateClient(new FakeHttp(JsonReply(true, content)));
        var service = new JobAnalysisService(CreateGemini(client), new SkillNormalizer());

        var error = await Assert.ThrowsAsync<AiProviderException>(() => service.ExtractRequirementsAsync("Listing"));
        Assert.Contains("job requirements", error.Message);
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task OllamaSingleToolReplaysTheCallAndReturnsOriginalToolResult()
    {
        var model = new FakeHttp(
            Response("""{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"analyze_job","arguments":{"jobDescription":"untrusted"}}}]}}"""),
            JsonReply(false, "Analysis complete."));
        var tools = new FakeHttp(Response("""{"matchScore":82}"""));
        using var client = CreateClient(model);
        var agent = new JobPilotAgentService(new OllamaModelProvider(client, "test-model"), new FakeClientFactory(tools));

        var reply = await agent.RunAsync(new AgentRequest("Analyze this.", "Original listing."));

        Assert.Equal("Analysis complete.", reply.Message);
        Assert.Equal(82, Assert.Single(reply.ExecutedTools).Result.GetProperty("matchScore").GetInt32());
        var toolRequest = Assert.Single(tools.Requests);
        Assert.Equal("/api/jobs/match", toolRequest.Path);
        Assert.Equal("Original listing.", toolRequest.Body.GetProperty("jobDescription").GetString());
        var history = model.Requests[1].Body.GetProperty("messages");
        Assert.True(history[2].TryGetProperty("tool_calls", out _));
        Assert.Equal("analyze_job", history[3].GetProperty("tool_name").GetString());
        using var replay = JsonDocument.Parse(history[3].GetProperty("content").GetString()!);
        Assert.Equal(82, replay.RootElement.GetProperty("matchScore").GetInt32());
    }

    [Fact]
    public async Task GeminiGroupsToolResponsesAndReplaysThoughtSignaturesAndCallIds()
    {
        var model = new FakeHttp(Response("""
            {
              "candidates": [{"finishReason":"STOP","content":{"role":"model","parts":[
                {"text":"private thought","thought":true},
                {"functionCall":{"id":"one","name":"analyze_job","args":{"jobDescription":"Listing"}},"thoughtSignature":"Zmlyc3Q="},
                {"functionCall":{"id":"two","name":"generate_resume","args":{"jobDescription":"Listing"}}}
              ]}}]
            }
            """), JsonReply(true, "Both tools finished."));
        var tools = new FakeHttp(Response("""{"matchScore":82}"""), Response("""{"fullName":"Test Candidate"}"""));
        using var client = CreateClient(model);
        var agent = new JobPilotAgentService(CreateGemini(client),
            new FakeClientFactory(tools));

        var reply = await agent.RunAsync(new AgentRequest("Analyze and draft.", "Original listing."));

        Assert.Equal("Both tools finished.", reply.Message);
        Assert.Equal(["analyze_job", "generate_resume"], reply.ExecutedTools.Select(tool => tool.ToolName));
        Assert.Equal("/api/resumes/generate", tools.Requests[1].Path);
        var declarations = model.Requests[0].Body.GetProperty("tools")[0].GetProperty("functionDeclarations");
        Assert.Equal(2, declarations.GetArrayLength());
        Assert.True(declarations[0].TryGetProperty("parametersJsonSchema", out _));
        var history = model.Requests[1].Body.GetProperty("contents");
        Assert.Equal(3, history.GetArrayLength());
        Assert.Equal("Zmlyc3Q=", history[1].GetProperty("parts")[1].GetProperty("thoughtSignature").GetString());
        Assert.Equal("user", history[2].GetProperty("role").GetString());
        var responseParts = history[2].GetProperty("parts");
        Assert.Equal("one", responseParts[0].GetProperty("functionResponse").GetProperty("id").GetString());
        var resumeResponse = responseParts[1].GetProperty("functionResponse");
        Assert.Equal("two", resumeResponse.GetProperty("id").GetString());
        Assert.True(resumeResponse.GetProperty("response").GetProperty("draftAvailable").GetBoolean());
        Assert.False(resumeResponse.GetProperty("response").TryGetProperty("fullName", out _));
    }

    [Fact]
    public async Task AgentPreservesSafeGeminiDiagnosticsFromAToolFailure()
    {
        const string toolCall = """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"analyze_job","args":{"jobDescription":"Listing"}}}]}}]}""";
        using var client = CreateClient(new FakeHttp(Response(toolCall)));
        await using var provider = CreateGemini(client);
        var toolResponse = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("""
                {"title":"AI request failed","detail":"Gemini denied access.","providerStatusCode":403,"diagnosticCode":"authentication_or_permission"}
                """, Encoding.UTF8, "application/problem+json")
        };
        var agent = new JobPilotAgentService(provider, new FakeClientFactory(new FakeHttp(toolResponse)));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => agent.RunAsync(new AgentRequest("Analyze.", "Listing")));

        Assert.Equal(403, error.ProviderStatusCode);
        Assert.Equal("authentication_or_permission", error.DiagnosticCode);
    }

    [Fact]
    public async Task RepeatedCallsRetainDuplicateProtectionAndIterationLimit()
    {
        const string packed = """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"analyze_job","args":{"jobDescription":"Listing"}}}]}}]}""";
        var model = new FakeHttp(Response(packed), Response(packed), Response(packed), Response(packed));
        var tools = new FakeHttp(Response("""{"matchScore":50}"""));
        using var client = CreateClient(model);
        var agent = new JobPilotAgentService(CreateGemini(client),
            new FakeClientFactory(tools));

        var reply = await agent.RunAsync(new AgentRequest("Analyze repeatedly.", "Original listing."));

        Assert.Contains("execution limit", reply.Message);
        Assert.Single(reply.ExecutedTools);
        Assert.Single(tools.Requests);
        Assert.Equal(4, model.Requests.Count);
        var history = model.Requests[2].Body.GetProperty("contents");
        var duplicate = history[4].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("response");
        Assert.Equal("Tool already executed.", duplicate.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 502, "invalid_request")]
    [InlineData(HttpStatusCode.Unauthorized, 502, "authentication_or_permission")]
    [InlineData(HttpStatusCode.Forbidden, 502, "authentication_or_permission")]
    [InlineData(HttpStatusCode.NotFound, 502, "model_unavailable")]
    [InlineData(HttpStatusCode.TooManyRequests, 429, "quota_or_rate_limit")]
    [InlineData(HttpStatusCode.InternalServerError, 502, "temporary_provider_failure")]
    [InlineData(HttpStatusCode.BadGateway, 502, "temporary_provider_failure")]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503, "temporary_provider_failure")]
    [InlineData(HttpStatusCode.GatewayTimeout, 504, "temporary_provider_failure")]
    public async Task ProviderErrorsAreSafeAndDoNotTriggerFallback(HttpStatusCode status, int apiStatus, string expected)
    {
        var handler = new FakeHttp(GoogleError(status, "PERMISSION_DENIED"));
        using var client = CreateClient(handler);
        var provider = CreateGemini(client);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateAsync(new AiRequest("", [new AiUserMessage("Test")])));

        Assert.Equal(apiStatus, error.StatusCode);
        Assert.Equal((int)status, error.ProviderStatusCode);
        Assert.Equal(expected, error.DiagnosticCode);
        Assert.DoesNotContain("test-key", error.Message);
        Assert.DoesNotContain("private candidate data", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GeminiRejectsMalformedProviderResponseAndHonorsCancellation()
    {
        using var client = CreateClient(new FakeHttp(Response("""{"candidates":[{}]}""")));
        var provider = CreateGemini(client);
        var input = new AiRequest("", [new AiUserMessage("Test")]);
        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateAsync(input));
        Assert.Contains("malformed", error.Message);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(input, canceled.Token));
    }

    [Fact]
    public async Task ProviderTimeoutIsReportedAsGatewayTimeout()
    {
        using var client = new HttpClient(new TimeoutHandler()) { BaseAddress = new Uri("https://test.invalid/") };
        var provider = new OllamaModelProvider(client, "test-model");

        var error = await Assert.ThrowsAsync<AiProviderException>(
            () => provider.GenerateAsync(new AiRequest("", [new AiUserMessage("Test")])));

        Assert.Equal(504, error.StatusCode);
        Assert.Contains("Ollama timed out", error.Message);
    }

    [Theory]
    [InlineData(null, typeof(OpenAiModelProvider))]
    [InlineData("OpenAI", typeof(OpenAiModelProvider))]
    [InlineData("oLLaMa", typeof(OllamaModelProvider))]
    [InlineData("Gemini", typeof(GeminiModelProvider))]
    public async Task DependencyInjectionSelectsOnlyTheConfiguredProvider(string? name, Type expectedType)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:Provider"] = name,
            ["AI:OpenAI:ApiKey"] = "test-openai-key",
            ["AI:Gemini:Model"] = "models/test-model",
            ["AI:Gemini:ApiKey"] = "test-key"
        }).Build();
        await using var services = new ServiceCollection().AddJobPilotAi(configuration).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();

        Assert.IsType(expectedType, Assert.Single(scope.ServiceProvider.GetServices<IAiModelProvider>()));
    }

    [Fact]
    public void GeminiRequiresConfiguredKeyAndDefaultsToFlashLite()
    {
        var settings = new Dictionary<string, string?> { ["AI:Provider"] = "Gemini" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.Contains("AI:Gemini:ApiKey",
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddJobPilotAi(config)).Message);
        config["AI:Gemini:ApiKey"] = "test-key";
        using var services = new ServiceCollection().AddJobPilotAi(config).BuildServiceProvider();
        Assert.Equal(new AiProviderSelection("Gemini", "gemini-3.1-flash-lite"),
            services.GetRequiredService<AiProviderSelection>());
    }

    [Fact]
    public async Task RetryHandlerRecoversFrom503AndPreservesSdkRequestWithoutLeakingErrorText()
    {
        var logger = new CapturingLogger();
        var failed = GoogleError(HttpStatusCode.ServiceUnavailable, "UNAVAILABLE");
        failed.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        var http = new FakeHttp(failed, JsonReply(true, "Hello JobPilot."));
        using var client = WithRetryHandler(http, logger);
        await using var provider = CreateGemini(client);

        var reply = await provider.GenerateAsync(Hello());

        Assert.Equal("Hello JobPilot.", reply.Text);
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(http.Requests[0].Path, http.Requests[1].Path);
        Assert.True(JsonElement.DeepEquals(http.Requests[0].Body, http.Requests[1].Body));
        Assert.Equal("test-key", http.Requests[1].ApiKey);
        var log = Assert.Single(logger.Messages);
        Assert.Contains("503", log);
        Assert.Contains("UNAVAILABLE", log);
        Assert.DoesNotContain("private candidate data", log);
        Assert.DoesNotContain("test-key", log);
    }

    [Fact]
    public async Task RetryHandlerStopsAfterThreeQuotaFailures()
    {
        var responses = Enumerable.Range(0, 3).Select(_ =>
        {
            var response = GoogleError(HttpStatusCode.TooManyRequests, "RESOURCE_EXHAUSTED");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        }).ToArray();
        var http = new FakeHttp(responses);
        using var client = WithRetryHandler(http, new CapturingLogger());
        await using var provider = CreateGemini(client);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateAsync(Hello()));

        Assert.Equal(429, error.ProviderStatusCode);
        Assert.Equal(3, http.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RetryHandlerDoesNotRetryPermanentErrorsOrAnExcessiveRetryAfter(HttpStatusCode status)
    {
        var response = GoogleError(status, "PERMISSION_DENIED");
        if (status == HttpStatusCode.ServiceUnavailable)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        var logger = new CapturingLogger();
        var http = new FakeHttp(response);
        using var client = WithRetryHandler(http, logger);
        await using var provider = CreateGemini(client);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateAsync(Hello()));

        Assert.Equal((int)status, error.ProviderStatusCode);
        Assert.Single(http.Requests);
        Assert.Contains("SERVICE_DISABLED", Assert.Single(logger.Messages));
    }

    [Fact]
    public async Task GatewayHtmlRetainsUpstreamStatus()
    {
        using var client = CreateClient(new FakeHttp(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<html>Service unavailable</html>", Encoding.UTF8, "text/html")
        }));
        await using var provider = CreateGemini(client);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateAsync(Hello()));

        Assert.Equal(503, error.ProviderStatusCode);
        Assert.Equal("temporary_provider_failure", error.DiagnosticCode);
    }

    [Fact]
    public async Task CancellationDuringBackoffPreventsAnotherRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var response = GoogleError(HttpStatusCode.ServiceUnavailable, "UNAVAILABLE");
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
        var http = new FakeHttp(response);
        using var client = WithRetryHandler(http, new CapturingLogger(cancellation.Cancel));
        await using var provider = CreateGemini(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(Hello(), cancellation.Token));
        Assert.Single(http.Requests);
    }

    [Theory]
    [InlineData("AI:Provider", "Other", "AI:Provider")]
    [InlineData("AI:Ollama:BaseUrl", "file:///not-a-server", "BaseUrl")]
    [InlineData("AI:Gemini:Model", "unsafe?model", "AI:Gemini:Model")]
    [InlineData("AI:Gemini:ApiKey", "", "AI:Gemini:ApiKey")]
    public void InvalidConfigurationFailsAtRegistration(string key, string value, string message)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AI:Provider"] = key.StartsWith("AI:Gemini:", StringComparison.Ordinal) ? "Gemini" : "Ollama",
            ["AI:Gemini:Model"] = "test-model",
            ["AI:Gemini:ApiKey"] = "test-key"
        };
        settings[key] = value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddJobPilotAi(config));
        Assert.Contains(message, error.Message);
        Assert.DoesNotContain("test-key", error.Message);
    }

    private static IAiModelProvider CreateProvider(bool gemini, HttpClient client) => gemini
        ? CreateGemini(client)
        : new OllamaModelProvider(client, "test-model");

    private static GeminiModelProvider CreateGemini(HttpClient client) =>
        GeminiModelProvider.Create(() => client, "test-model", "test-key");

    private static AiRequest Hello() => new("", [new AiUserMessage("Say Hello JobPilot.")]);

    private static HttpClient WithRetryHandler(FakeHttp handler, CapturingLogger logger) =>
        new(new GeminiRetryHandler(logger) { InnerHandler = handler });

    private static HttpResponseMessage GoogleError(HttpStatusCode code, string status) =>
        Response(JsonSerializer.Serialize(new { error = new
        {
            code = (int)code, status, message = "test-key; private candidate data",
            details = new[] { new Dictionary<string, string>
            {
                ["@type"] = "type.googleapis.com/google.rpc.ErrorInfo",
                ["reason"] = "SERVICE_DISABLED"
            } }
        } }), code);

    private static HttpClient CreateClient(FakeHttp handler) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("https://test.invalid/") };

    private static HttpResponseMessage JsonReply(bool gemini, string text) => Response(gemini
        ? JsonSerializer.Serialize(new { candidates = new[] { new { content = new { role = "model", parts = new[] { new { text } } } } } })
        : JsonSerializer.Serialize(new { message = new { role = "assistant", content = text } }));

    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeClientFactory(FakeHttp handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => AiProviderTests.CreateClient(handler);
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException());
    }

    private sealed class CapturingLogger(Action? onMessage = null) : ILogger<GeminiRetryHandler>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            onMessage?.Invoke();
        }
    }

    private sealed class FakeHttp(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public List<(string Path, JsonElement Body, string? ApiKey)> Requests { get; } = [];
        private readonly Queue<HttpResponseMessage> pending = new(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var key = request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null;
            Requests.Add((request.RequestUri!.AbsolutePath, document.RootElement.Clone(), key));
            return pending.Dequeue();
        }
    }
}
