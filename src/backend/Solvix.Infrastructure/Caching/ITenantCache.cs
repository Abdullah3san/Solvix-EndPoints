namespace Solvix.Infrastructure.Caching;

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Tenant-aware cache facade. Every key is prefixed with the ambient tenant id
/// so cached data can never be served to another tenant, including from
/// background jobs that run inside a tenant scope.
/// </summary>
public interface ITenantCache
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

internal sealed class TenantCache(IDistributedCache cache, ITenantContext tenantContext) : ITenantCache
{
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var bytes = await cache.GetAsync(Scoped(key), cancellationToken);
        return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        cache.SetAsync(
            Scoped(key),
            JsonSerializer.SerializeToUtf8Bytes(value),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken);

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(Scoped(key), cancellationToken);

    private string Scoped(string key) => $"tenant:{tenantContext.RequireTenantId()}:{key}";
}
