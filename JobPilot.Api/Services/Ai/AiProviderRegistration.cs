using System.Text.RegularExpressions;

namespace JobPilot.Api.Services.Ai;

internal static class AiProviderRegistration
{
    public static IServiceCollection AddJobPilotAi(this IServiceCollection services, IConfiguration configuration)
    {
        const string clientName = "JobPilotAi";
        var provider = configuration["AI:Provider"] ?? "OpenAI";
        if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            var model = configuration["AI:OpenAI:Model"] ?? "gpt-6-luna";
            if (!Regex.IsMatch(model, @"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z"))
                throw new InvalidOperationException("Set AI:OpenAI:Model to an OpenAI API model ID.");

            var apiKey = configuration["AI:OpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(character => character < '!' || character > '~'))
                throw new InvalidOperationException("Set AI:OpenAI:ApiKey using JobPilot.Api User Secrets or an environment variable.");

            var maxTokens = ReadLimit(configuration, "AI:OpenAI:MaxOutputTokens", 4096, 128, 32768);
            var timeoutSeconds = ReadLimit(configuration, "AI:OpenAI:RequestTimeoutSeconds", 60, 1, 120);
            var settings = new OpenAiProviderSettings(model, maxTokens, TimeSpan.FromSeconds(timeoutSeconds));
            AddClient(services, clientName, new Uri("https://api.openai.com/"))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            services.AddSingleton(new AiProviderSelection("OpenAI", model));
            services.AddScoped<IAiModelProvider>(scope =>
                new OpenAiModelProvider(scope.GetRequiredService<IHttpClientFactory>().CreateClient(clientName),
                    apiKey, settings, scope.GetRequiredService<ILogger<OpenAiModelProvider>>()));
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
