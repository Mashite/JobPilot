namespace JobPilot.Api.Services.Ai;

internal sealed class OpenAiUsage(IConfiguration configuration)
{
    private readonly object sync = new();
    private readonly decimal? inputPrice = ReadPrice(configuration["AI:OpenAI:InputUsdPerMillionTokens"]);
    private readonly decimal? outputPrice = ReadPrice(configuration["AI:OpenAI:OutputUsdPerMillionTokens"]);
    private long requests, inputTokens, outputTokens, timedMilliseconds, requestsWithUsage, reasoningTokens, tokenRetryRequests;

    public void Record(int? input, int? output, long elapsedMilliseconds, int? reasoning = null, bool tokenRetry = false)
    {
        lock (sync)
        {
            requests++;
            timedMilliseconds += elapsedMilliseconds;
            inputTokens += input.GetValueOrDefault();
            outputTokens += output.GetValueOrDefault();
            reasoningTokens += reasoning.GetValueOrDefault();
            if (tokenRetry)
                tokenRetryRequests++;
            if (input.HasValue && output.HasValue)
                requestsWithUsage++;
        }
    }

    public object Snapshot()
    {
        lock (sync)
        {
            decimal? estimatedUsd = inputPrice is { } input && outputPrice is { } output && requestsWithUsage > 0
                ? (inputTokens * input + outputTokens * output) / 1_000_000 : null;
            return new { requests, inputTokens, outputTokens, reasoningTokens, tokenRetryRequests, requestsWithUsage, timedMilliseconds, estimatedUsd };
        }
    }

    private static decimal? ReadPrice(string? value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture,
            out var price) && price >= 0 ? price : null;
}
