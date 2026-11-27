namespace JobPilot.Api.Services;

internal sealed record SemanticMatchingOptions(int BatchSize = 6, int MaxOutputTokens = 8192, int MaxRetryAttempts = 1,
    int RetryTokenLimit = 16384)
{
    public static SemanticMatchingOptions Read(IConfiguration configuration, int modelLimit, int applicationLimit, int retryLimit,
        int semanticTokenLimit)
    {
        const string prefix = "AI:SemanticMatching:";
        int ReadNumber(string suffix, int fallback, int minimum, int maximum)
        {
            var key = prefix + suffix;
            if (configuration[key] is not { } value)
                return fallback;
            if (int.TryParse(value, out var number) && number >= minimum && number <= maximum)
                return number;
            throw new InvalidOperationException($"{key} must be an integer between {minimum} and {maximum}.");
        }

        return new(ReadNumber("BatchSize", 6, 1, 16),
            Math.Min(applicationLimit, ReadNumber("MaxOutputTokens", semanticTokenLimit, 128, modelLimit)),
            ReadNumber("MaxRetryAttempts", 1, 0, 1), retryLimit);
    }
}
