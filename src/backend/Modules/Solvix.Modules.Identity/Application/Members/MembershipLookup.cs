namespace Solvix.Modules.Identity.Application.Members;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;

public interface IMembershipCache
{
    Task InvalidateAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Membership lookup used on every authenticated request. Cached in Redis with
/// a short TTL and invalidated on any membership change, so a revoked member is
/// locked out within seconds even though their JWT is still valid.
/// </summary>
internal sealed class CachedMembershipLookup(ISolvixDbContext db, IDistributedCache cache)
    : IMembershipLookup, IMembershipCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<MembershipDescriptor?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var key = CacheKey(tenantId, userId);
        var cached = await cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<MembershipDescriptor>(cached);
        }

        // Runs in the tenant scope of the caller: the query filter + RLS restrict
        // this lookup to the tenant the token was issued for.
        var membership = await db.Set<TenantMembership>()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.UserId == userId)
            .Select(m => new MembershipDescriptor(m.TenantId, m.UserId, m.Role, m.IsActive))
            .FirstOrDefaultAsync(cancellationToken);

        if (membership is not null)
        {
            await cache.SetAsync(
                key,
                JsonSerializer.SerializeToUtf8Bytes(membership),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl },
                cancellationToken);
        }

        return membership;
    }

    public Task InvalidateAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        cache.RemoveAsync(CacheKey(tenantId, userId), cancellationToken);

    private static string CacheKey(Guid tenantId, Guid userId) => $"solvix:tenant:{tenantId}:membership:{userId}";
}
