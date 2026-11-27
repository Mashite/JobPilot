using System.Security.Cryptography;
using System.Text;
using JobPilot.Api.Models;
using Microsoft.Extensions.Caching.Memory;

namespace JobPilot.Api.Services;

public sealed class JobRequirementCache : IDisposable
{
    private readonly MemoryCache items = new(new MemoryCacheOptions { SizeLimit = 64 });

    internal static string Key(string description) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description)));

    internal JobAnalysis? Find(string key) => items.Get<JobAnalysis>(key);

    internal void Store(string key, JobAnalysis analysis) => items.Set(key, analysis,
        new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) });

    public void Dispose() => items.Dispose();
}
