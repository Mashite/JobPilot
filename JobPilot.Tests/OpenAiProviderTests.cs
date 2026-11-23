using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JobPilot.Api.Models.Agent;
using JobPilot.Api.Services;
using JobPilot.Api.Services.Agent;
using JobPilot.Api.Services.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobPilot.Tests;

public sealed class OpenAiProviderTests
{
    [Fact]
    public void DefaultsToLunaAndRequiresKeyOnlyWhenOpenAiIsSelected()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Assert.Contains("AI:OpenAI:ApiKey",
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddJobPilotAi(config)).Message);
        config["AI:OpenAI:ApiKey"] = "test-key";
        using var services = new ServiceCollection().AddJobPilotAi(config).BuildServiceProvider();
        using var scope = services.CreateScope();
        Assert.Equal(new AiProviderSelection("OpenAI", "gpt-6-luna"), services.GetRequiredService<AiProviderSelection>());
        Assert.IsType<OpenAiModelProvider>(Assert.Single(scope.ServiceProvider.GetServices<IAiModelProvider>()));
    }

    [Theory]
    [InlineData("AI:OpenAI:Model", "not a model")]
    [InlineData("AI:OpenAI:MaxOutputTokens", "0")]
    [InlineData("AI:OpenAI:RequestTimeoutSeconds", "121")]
    public void InvalidOpenAiSettingsAreRejectedBeforeAnyRequest(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:Provider"] = "OpenAI",
            ["AI:OpenAI:ApiKey"] = "test-key",
            [key] = value
        }).Build();
        Assert.Contains(key, Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddJobPilotAi(config)).Message);
    }

    [Fact]
    public async Task UsesResponsesEndpointAndLogsUsageWithoutContent()
    {
        var handler = new MockHttp(Reply("Hello JobPilot."));
        var logger = new CaptureLog();
        using var provider = CreateProvider(handler, logger, maxTokens: 256);

        var result = await provider.GenerateAsync(new AiRequest("", [new AiUserMessage("private prompt")], MaxOutputTokens: 700));

        Assert.Equal("Hello JobPilot.", result.Text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/responses", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer test-key", request.Authorization);
        Assert.Equal("gpt-6-luna", request.Body.GetProperty("model").GetString());
        Assert.Equal(256, request.Body.GetProperty("max_output_tokens").GetInt32());
        Assert.False(request.Body.GetProperty("store").GetBoolean());
        Assert.Equal("none", request.Body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(request.Body.TryGetProperty("temperature", out _));
        var log = Assert.Single(logger.Messages);
        Assert.Contains("input tokens 11 output tokens 3 total tokens 14", log);
        Assert.Contains("completed", log);
        Assert.DoesNotContain("private prompt", log);
        Assert.DoesNotContain("Hello JobPilot", log);
        Assert.DoesNotContain("test-key", log);
    }

    [Fact]
    public async Task ExtractsStructuredJobRequirementsWithoutChangingTheMatcher()
    {
        var handler = new MockHttp(Reply("""
            {"jobTitle":"Developer","requirements":[{"skill":"C#","importance":"required"},{"skill":"Docker","importance":"preferred"}],
            "experienceRequirements":["Two years"],"technologies":[".NET"],"responsibilities":["Maintain APIs"]}
            """));
        using var provider = CreateProvider(handler);
        var analysis = await new JobAnalysisService(provider, new SkillNormalizer()).ExtractRequirementsAsync("Example listing");

        Assert.Equal(["Two years"], analysis.ExperienceRequirements);
        Assert.Equal([".NET"], analysis.Technologies);
        Assert.Equal(["Maintain APIs"], analysis.Responsibilities);
        var match = new JobMatchingService(new SkillNormalizer()).Match(analysis, new() { TechnicalSkills = ["C#"] });
        Assert.Equal(75, match.MatchScore);
        Assert.Equal(["C#"], match.MatchingSkills);
        Assert.Equal(["Docker"], match.MissingOrUnverifiedSkills);
        Assert.Equal(analysis.Responsibilities, match.Responsibilities);
        var format = Assert.Single(handler.Requests).Body.GetProperty("text").GetProperty("format");
        Assert.True(format.GetProperty("strict").GetBoolean());
        var required = format.GetProperty("schema").GetProperty("required").EnumerateArray().Select(item => item.GetString());
        Assert.Contains("responsibilities", required);
        Assert.Contains("experienceRequirements", required);
        Assert.Contains("technologies", required);
    }

    [Theory]
    [InlineData(400, "invalid_request")]
    [InlineData(401, "invalid_api_key")]
    [InlineData(403, "model_access_denied")]
    [InlineData(404, "model_not_found")]
    [InlineData(429, "insufficient_quota")]
    public async Task NonTransientErrorsAreSafeAndDoNotRetry(int status, string expectedCode)
    {
        var handler = new MockHttp(Error(status, status == 429 ? "insufficient_quota" : "untrusted", "key and private data"));
        var logger = new CaptureLog();
        using var provider = CreateProvider(handler, logger);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateAsync(Hello()));

        Assert.Equal(status, error.ProviderStatusCode);
        Assert.Equal(expectedCode, error.DiagnosticCode);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("private data", error.Message);
        Assert.DoesNotContain("private data", Assert.Single(logger.Messages));
        if (status is 403 or 404)
            Assert.Contains("gpt-6-luna", error.Message);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task TransientErrorsRetrySameModelAndRespectRetryAfter(int status)
    {
        var transient = Error(status, "rate_limit_exceeded", "Transient error");
        transient.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        var handler = new MockHttp(transient, Reply("Hello JobPilot."));
        var logger = new CaptureLog();
        using var provider = CreateProvider(handler, logger);

        Assert.Equal("Hello JobPilot.", (await provider.GenerateAsync(Hello())).Text);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, logger.Messages.Count);
        Assert.True(JsonElement.DeepEquals(handler.Requests[0].Body, handler.Requests[1].Body));
    }

    [Fact]
    public async Task RetriesAreBoundedAndDoNotRetryEarlyWhenAskedToWaitLonger()
    {
        static HttpResponseMessage Busy(int seconds)
        {
            var response = Error(503, "server_error", "Busy");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return response;
        }

        var exhausted = new MockHttp(Busy(0), Busy(0), Busy(0));
        using var provider = CreateProvider(exhausted);
        Assert.Equal(503, (await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateAsync(Hello()))).ProviderStatusCode);
        Assert.Equal(3, exhausted.Requests.Count);

        var longWait = new MockHttp(Busy(60));
        using var other = CreateProvider(longWait);
        await Assert.ThrowsAsync<AiProviderException>(() => other.GenerateAsync(Hello()));
        Assert.Single(longWait.Requests);
    }

    [Fact]
    public async Task AgentReplaysBothToolsAndCallIdsWithoutExecutingADuplicateResume()
    {
        var response = Completion([
            new { type = "reasoning", id = "rs_1", summary = Array.Empty<string>(), encrypted_content = "encrypted" },
            Call("call_1", "analyze_job", """{"jobDescription":"model copy"}"""),
            Call("call_2", "generate_resume", """{"jobDescription":"model copy"}"""),
            Call("call_3", "generate_resume", """{"jobDescription":"model copy"}""")
        ]);
        var openAi = new MockHttp(response, Reply("Analysis and draft complete."));
        var tools = new MockHttp(JsonResponse("""{"matchScore":75}"""), JsonResponse("""{"fullName":"Verified Person"}"""));
        using var provider = CreateProvider(openAi);
        var agent = new JobPilotAgentService(provider, new ToolClientFactory(tools));

        var result = await agent.RunAsync(new AgentRequest("Analyze and draft.", "Authoritative listing"));

        Assert.Equal("Analysis and draft complete.", result.Message);
        Assert.Equal(["analyze_job", "generate_resume"], result.ExecutedTools.Select(tool => tool.ToolName));
        Assert.Equal("Verified Person", result.ExecutedTools[1].Result.GetProperty("fullName").GetString());
        Assert.Equal(["/api/jobs/match", "/api/resumes/generate"], tools.Requests.Select(request => request.Uri.AbsolutePath));
        Assert.All(tools.Requests, request => Assert.Equal("Authoritative listing", request.Body.GetProperty("jobDescription").GetString()));
        var declarations = openAi.Requests[0].Body.GetProperty("tools");
        Assert.Equal(2, declarations.GetArrayLength());
        Assert.True(declarations[0].GetProperty("strict").GetBoolean());
        var history = openAi.Requests[1].Body.GetProperty("input");
        Assert.Equal("encrypted", history[1].GetProperty("encrypted_content").GetString());
        Assert.Equal("call_2", history[3].GetProperty("call_id").GetString());
        Assert.Equal("call_1", history[5].GetProperty("call_id").GetString());
        Assert.Equal("call_2", history[6].GetProperty("call_id").GetString());
        Assert.Contains("Tool already executed.", history[7].GetProperty("output").GetString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"jobDescription":42}""")]
    [InlineData("""{"jobDescription":"valid","unexpected":true}""")]
    public async Task InvalidToolArgumentsAreReturnedToTheModelWithoutExecuting(string arguments)
    {
        var openAi = new MockHttp(Completion([Call("invalid_1", "generate_resume", arguments)]), Reply("Please try again."));
        var tools = new MockHttp();
        using var provider = CreateProvider(openAi);
        var agent = new JobPilotAgentService(provider, new ToolClientFactory(tools));

        var result = await agent.RunAsync(new AgentRequest("Draft a resume.", "Authoritative listing"));

        Assert.Empty(result.ExecutedTools);
        Assert.Empty(tools.Requests);
        var output = openAi.Requests[1].Body.GetProperty("input")[2];
        Assert.Equal("invalid_1", output.GetProperty("call_id").GetString());
        Assert.Contains("non-empty jobDescription", output.GetProperty("output").GetString());
    }

    [Fact]
    public async Task CallerCancellationPreventsRequests()
    {
        var handler = new MockHttp();
        using var provider = CreateProvider(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(Hello(), cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    private static OpenAiModelProvider CreateProvider(MockHttp handler, ILogger<OpenAiModelProvider>? logger = null, int maxTokens = 4096) =>
        new(new HttpClient(handler), "test-key", new OpenAiProviderSettings("gpt-6-luna", maxTokens, TimeSpan.FromSeconds(10)),
            logger ?? NullLogger<OpenAiModelProvider>.Instance);

    private static AiRequest Hello() => new("", [new AiUserMessage("Say Hello JobPilot.")]);
    private static object Call(string callId, string tool, string arguments) =>
        new { type = "function_call", id = "fc_" + callId, call_id = callId, name = tool, arguments, status = "completed" };

    private static HttpResponseMessage Reply(string content) => Completion([
        new { type = "message", id = "msg_test", role = "assistant", status = "completed",
            content = new[] { new { type = "output_text", text = content, annotations = Array.Empty<object>() } } }
    ]);

    private static HttpResponseMessage Completion(object[] output) => JsonResponse(JsonSerializer.Serialize(new
    {
        id = "resp_test", @object = "response", status = "completed", output,
        usage = new { input_tokens = 11, output_tokens = 3, total_tokens = 14 }
    }));

    private static HttpResponseMessage Error(int status, string code, string message) =>
        JsonResponse(JsonSerializer.Serialize(new { error = new { code, message } }), status);

    private static HttpResponseMessage JsonResponse(string body, int status = 200) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class MockHttp(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> pending = new(responses);
        public List<(Uri Uri, JsonElement Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add((request.RequestUri!, document.RootElement.Clone(), request.Headers.Authorization?.ToString()));
            return pending.Dequeue();
        }
    }

    private sealed class ToolClientFactory(MockHttp handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class CaptureLog : ILogger<OpenAiModelProvider>
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
