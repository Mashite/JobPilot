using Microsoft.Extensions.Caching.Memory;
using JobPilot.Api.Models;

namespace JobPilot.Api.Services;

internal sealed class SemanticMatchCache : IDisposable
{
    private readonly MemoryCache entries = new(new MemoryCacheOptions { SizeLimit = 64 });

    public JobMatchResult? Find(string key) => entries.Get<JobMatchResult>(key);

    public void Store(string key, JobMatchResult result) =>
        entries.Set(key, result, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
        });

    public void Dispose() => entries.Dispose();
}
