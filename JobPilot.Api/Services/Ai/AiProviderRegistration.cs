using System.Text.RegularExpressions;

namespace JobPilot.Api.Services.Ai;

internal static class AiProviderRegistration
{
    public static IServiceCollection AddJobPilotAi(this IServiceCollection services, IConfiguration configuration)
    {
        const string clientName = "JobPilotAi";
        var provider = configuration["AI:Provider"] ?? "Ollama";
        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
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
            services.AddScoped<IAiModelProvider>(scope =>
                new OllamaModelProvider(scope.GetRequiredService<IHttpClientFactory>().CreateClient(clientName), model));
        }
        else if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            var model = configuration["AI:Gemini:Model"];
            if (model?.StartsWith("models/", StringComparison.Ordinal) == true)
                model = model["models/".Length..];
            if (model is null || !Regex.IsMatch(model, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z"))
                throw new InvalidOperationException("Set AI:Gemini:Model to a Gemini model ID.");

            var apiKey = configuration["AI:Gemini:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(character => character < '!' || character > '~'))
                throw new InvalidOperationException("Set AI:Gemini:ApiKey using User Secrets or an environment variable.");

            AddClient(services, clientName, new Uri("https://generativelanguage.googleapis.com/"))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            services.AddScoped<IAiModelProvider>(scope =>
                new GeminiModelProvider(scope.GetRequiredService<IHttpClientFactory>().CreateClient(clientName), model, apiKey));
        }
        else
        {
            throw new InvalidOperationException("AI:Provider must be Ollama or Gemini.");
        }

        return services;
    }

    private static IHttpClientBuilder AddClient(IServiceCollection services, string name, Uri baseUrl) =>
        services.AddHttpClient(name, client =>
            {
                client.BaseAddress = baseUrl;
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .RemoveAllLoggers();
}
