namespace Solvix.Modules.Tenancy.Application.Directory;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface ITenantDirectoryCache
{
    Task InvalidateAsync(Guid tenantId, string slug, CancellationToken cancellationToken);
}

/// <summary>
/// Platform-level tenant lookup backed by Redis. Used on every request by the
/// tenant resolution middleware, so hits must be cheap.
/// </summary>
internal sealed class CachedTenantDirectory(ISolvixDbContext db, IDistributedCache cache) : ITenantDirectory, ITenantDirectoryCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public async Task<TenantDescriptor?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var key = IdKey(tenantId);
        var cached = await cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<TenantDescriptor>(cached);
        }

        var tenant = await db.Set<Tenant>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        var descriptor = tenant.ToDescriptor();
        await StoreAsync(descriptor, cancellationToken);
        return descriptor;
    }

    public async Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slug);
        var normalized = slug.Trim().ToLowerInvariant();
        var cached = await cache.GetAsync(SlugKey(normalized), cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<TenantDescriptor>(cached);
        }

        var tenant = await db.Set<Tenant>().AsNoTracking().FirstOrDefaultAsync(t => t.Slug == normalized, cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        var descriptor = tenant.ToDescriptor();
        await StoreAsync(descriptor, cancellationToken);
        return descriptor;
    }

    public async Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(CancellationToken cancellationToken) =>
        await db.Set<Tenant>().AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active)
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

    public async Task InvalidateAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(IdKey(tenantId), cancellationToken);
        await cache.RemoveAsync(SlugKey(slug), cancellationToken);
    }

    private async Task StoreAsync(TenantDescriptor descriptor, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(descriptor);
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl };
        await cache.SetAsync(IdKey(descriptor.Id), payload, options, cancellationToken);
        await cache.SetAsync(SlugKey(descriptor.Slug), payload, options, cancellationToken);
    }

    private static string IdKey(Guid id) => $"solvix:platform:tenant:id:{id}";

    private static string SlugKey(string slug) => $"solvix:platform:tenant:slug:{slug}";
}
