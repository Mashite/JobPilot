using System.Text.RegularExpressions;

namespace JobPilot.Api.Services.Ai;

internal static class AiProviderRegistration
{
    public static IServiceCollection AddJobPilotAi(this IServiceCollection services, IConfiguration configuration)
    {
        const string clientName = "JobPilotAi";
        services.AddSingleton(_ => new OpenAiUsage(configuration));
        var provider = configuration["AI:Provider"] ?? "OpenAI";
        if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            var model = configuration["AI:OpenAI:Model"] ?? "gpt-6-luna";
            if (!Regex.IsMatch(model, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z"))
                throw new InvalidOperationException("Set AI:OpenAI:Model to an OpenAI API model ID.");

            var apiKey = configuration["AI:OpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(character => character < '!' || character > '~'))
                throw new InvalidOperationException("Set AI:OpenAI:ApiKey using JobPilot.Api User Secrets or an environment variable.");

            const int documentedLunaLimit = 128_000;
            var isLuna = model.Equals("gpt-6-luna", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("gpt-6-luna-", StringComparison.OrdinalIgnoreCase);
            var modelLimit = ReadLimit(configuration, "AI:OpenAI:ModelOutputLimit",
                isLuna ? documentedLunaLimit : 16_384, 128, documentedLunaLimit);
            var maxTokens = ReadLimit(configuration, "AI:OpenAI:MaxOutputTokens", Math.Min(16_384, modelLimit), 128, modelLimit);
            var retryLimit = ReadLimit(configuration, "AI:OpenAI:TokenRetryLimit", maxTokens, 128, maxTokens);
            var defaults = new Dictionary<AiOperation, int>
            {
                [AiOperation.Agent] = 4096, [AiOperation.JobAnalysis] = 8192, [AiOperation.SemanticMatching] = 8192,
                [AiOperation.ResumeGeneration] = 8192, [AiOperation.FinalResponse] = 2048, [AiOperation.CandidateEvaluation] = 6000
            };
            var limits = defaults.ToDictionary(item => item.Key, item => Math.Min(maxTokens,
                ReadLimit(configuration, $"AI:OpenAI:TokenLimits:{item.Key}", item.Value, 128, modelLimit)));
            var efforts = new Dictionary<AiOperation, string>();
            foreach (var operation in defaults.Keys)
            {
                var key = $"AI:OpenAI:ReasoningEffort:{operation}";
                var effort = configuration[key] ?? (isLuna
                    ? operation is AiOperation.SemanticMatching or AiOperation.CandidateEvaluation ? "low" : "none" : null);
                if (effort is null)
                    continue;
                if (effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max") ||
                    (isLuna && effort == "minimal"))
                    throw new InvalidOperationException($"{key} is not supported for the configured model.");
                efforts.Add(operation, effort);
            }
            var timeoutSeconds = ReadLimit(configuration, "AI:OpenAI:RequestTimeoutSeconds", 60, 1, 120);
            var settings = new OpenAiProviderSettings(model, maxTokens, TimeSpan.FromSeconds(timeoutSeconds), limits, retryLimit, efforts);
            services.AddSingleton(SemanticMatchingOptions.Read(configuration, modelLimit, maxTokens, retryLimit,
                limits[AiOperation.SemanticMatching]));
            AddClient(services, clientName, new Uri("https://api.openai.com/"))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            services.AddSingleton(new AiProviderSelection("OpenAI", model));
            services.AddScoped<IAiModelProvider>(scope =>
                new OpenAiModelProvider(scope.GetRequiredService<IHttpClientFactory>().CreateClient(clientName),
                    apiKey, settings, scope.GetRequiredService<ILogger<OpenAiModelProvider>>(), scope.GetRequiredService<OpenAiUsage>()));
        }
        else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            var model = configuration["AI:Ollama:Model"] ?? "qwen2.5:3b";
            if (!Regex.IsMatch(model, @"\A[A-Za-z0-9_./:-]+\z"))
                throw new InvalidOperationException("AI:Ollama:Model must contain a valid Ollama model name.");

            var url = configuration["AI:Ollama:BaseUrl"] ?? "http://localhost:11434/";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUrl) ||
                baseUrl.Scheme is not ("http" or "https") || baseUrl.UserInfo.Length > 0 ||
                baseUrl.Query.Length > 0 || baseUrl.Fragment.Length > 0)
            {
                throw new InvalidOperationException("AI:Ollama:BaseUrl must be an absolute HTTP or HTTPS URL without credentials, a query, or a fragment.");
            }

            AddClient(services, clientName, new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/"));
            services.AddSingleton(new AiProviderSelection("Ollama", model));
            services.AddScoped<IAiModelProvider>(scope =>
                new OllamaModelProvider(scope.GetRequiredService<IHttpClientFactory>().CreateClient(clientName), model));
        }
        else if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            var model = configuration["AI:Gemini:Model"] ?? "gemini-3.1-flash-lite";
            if (model.StartsWith("models/", StringComparison.Ordinal))
                model = model["models/".Length..];
            if (!Regex.IsMatch(model, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z"))
                throw new InvalidOperationException("Set AI:Gemini:Model to a Gemini model ID.");

            var apiKey = configuration["AI:Gemini:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(character => character < '!' || character > '~'))
                throw new InvalidOperationException("Set AI:Gemini:ApiKey using User Secrets or an environment variable.");

            services.AddTransient<GeminiRetryHandler>();
            AddClient(services, clientName, new Uri("https://generativelanguage.googleapis.com/"))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
                .AddHttpMessageHandler<GeminiRetryHandler>();
            services.AddSingleton(new AiProviderSelection("Gemini", model));
            services.AddScoped<IAiModelProvider>(scope =>
            {
                var clientFactory = scope.GetRequiredService<IHttpClientFactory>();
                return GeminiModelProvider.Create(() => clientFactory.CreateClient(clientName), model, apiKey);
            });
        }
        else
        {
            throw new InvalidOperationException("AI:Provider must be OpenAI, Ollama, or Gemini.");
        }

        return services;
    }

    private static int ReadLimit(IConfiguration configuration, string key, int defaultValue, int minimum, int maximum)
    {
        if (configuration[key] is not { } value)
            return defaultValue;
        if (int.TryParse(value, out var result) && result >= minimum && result <= maximum)
            return result;
        throw new InvalidOperationException($"{key} must be an integer between {minimum} and {maximum}.");
    }

    private static IHttpClientBuilder AddClient(IServiceCollection services, string name, Uri baseUrl) =>
        services.AddHttpClient(name, client =>
            {
                client.BaseAddress = baseUrl;
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .RemoveAllLoggers();
}

internal sealed record AiProviderSelection(string Name, string Model);
