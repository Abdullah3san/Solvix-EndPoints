namespace Solvix.Modules.Identity.Application.Access;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;

/// <summary>
/// Drops cached access facts after a change so it applies to the very next
/// request. Each call removes the entry immediately and again once the current
/// unit of work has committed (see <see cref="AccessCacheInvalidations"/>): an
/// entry re-read by a concurrent request before the commit cannot survive it.
/// </summary>
public interface IAccessCache
{
    Task InvalidateUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    Task InvalidateSessionAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken);

    Task InvalidatePolicyAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Reads a tenant's security policy (cached). Used on the request path and when sessions are created.</summary>
public interface ISecurityPolicyReader
{
    Task<SecurityPolicySettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Answers, for every authenticated request: is this session still alive, is the
/// caller still an active member, and which permissions do their roles grant
/// right now? Nothing here comes from the access token beyond the three ids, so
/// revoking a session, deactivating a member or changing a role is effective
/// immediately even though the token itself is still cryptographically valid.
///
/// The three facts are cached separately in Redis for a short time and
/// invalidated on change; the TTL bounds staleness if an invalidation is lost.
/// </summary>
internal sealed class AccessResolver(ISolvixDbContext db, IDistributedCache cache, IClock clock, AccessCacheInvalidations pending)
    : IAccessResolver, IAccessCache, ISecurityPolicyReader
{
    private static readonly TimeSpan AccessTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SessionTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PolicyTtl = TimeSpan.FromMinutes(5);

    public async Task<AccessResolution> ResolveAsync(Guid tenantId, Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var session = await FindSessionAsync(tenantId, sessionId, cancellationToken);
        if (session is null
            || session.UserId != userId
            || session.RevokedAtUtc is not null
            || now >= session.ExpiresAtUtc
            || now >= session.IdleExpiresAtUtc)
        {
            return AccessResolution.Denied(AccessStatus.SessionInvalid);
        }

        var access = await FindAccessAsync(tenantId, userId, cancellationToken);
        if (access is null || !access.MembershipActive)
        {
            return AccessResolution.Denied(AccessStatus.MembershipInactive);
        }

        if (!access.UserActive)
        {
            return AccessResolution.Denied(AccessStatus.UserInactive);
        }

        var mfaEnrollmentRequired = false;
        if (!session.MfaVerified)
        {
            var policy = await FindAsync(tenantId, cancellationToken);
            mfaEnrollmentRequired = policy?.RequireMfa == true;
        }

        return new AccessResolution(
            AccessStatus.Granted,
            new AccessGrant(
                access.MembershipId,
                sessionId,
                access.Permissions.ToHashSet(StringComparer.Ordinal),
                access.Roles,
                session.MfaVerified,
                mfaEnrollmentRequired));
    }

    public async Task<IReadOnlySet<string>> ResolveMemberPermissionsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var access = await FindAccessAsync(tenantId, userId, cancellationToken);
        return access is { MembershipActive: true, UserActive: true }
            ? access.Permissions.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public async Task<SecurityPolicySettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var key = PolicyKey(tenantId);
        var cached = await cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<SecurityPolicySettings>(cached);
        }

        // Explicit tenant predicate: this is also called from platform scope (sign-in), where the query filter is lifted.
        var policy = await db.Set<TenantSecurityPolicy>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);
        if (policy is null)
        {
            return null;
        }

        var settings = policy.ToSettings();
        await SetAsync(key, settings, PolicyTtl, cancellationToken);
        return settings;
    }

    public Task InvalidateUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        InvalidateAsync(AccessKey(tenantId, userId), cancellationToken);

    public Task InvalidateSessionAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken) =>
        InvalidateAsync(SessionKey(tenantId, sessionId), cancellationToken);

    public Task InvalidatePolicyAsync(Guid tenantId, CancellationToken cancellationToken) =>
        InvalidateAsync(PolicyKey(tenantId), cancellationToken);

    private Task InvalidateAsync(string key, CancellationToken cancellationToken)
    {
        pending.Add(key);
        return cache.RemoveAsync(key, cancellationToken);
    }

    private async Task<SessionSnapshot?> FindSessionAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        var key = SessionKey(tenantId, sessionId);
        var cached = await cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<SessionSnapshot>(cached);
        }

        // Runs in the tenant scope of the request: the query filter and RLS already
        // restrict this to the token's tenant; the predicate states it once more.
        var session = await db.Set<UserSession>()
            .AsNoTracking()
            .Where(s => s.Id == sessionId && s.TenantId == tenantId)
            .Select(s => new SessionSnapshot(s.UserId, s.ExpiresAtUtc, s.IdleExpiresAtUtc, s.RevokedAtUtc, s.MfaVerified))
            .FirstOrDefaultAsync(cancellationToken);
        if (session is not null)
        {
            await SetAsync(key, session, SessionTtl, cancellationToken);
        }

        return session;
    }

    private async Task<AccessSnapshot?> FindAccessAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var key = AccessKey(tenantId, userId);
        var cached = await cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<AccessSnapshot>(cached);
        }

        var membership = await db.Set<TenantMembership>()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.UserId == userId)
            .Select(m => new { m.Id, m.IsActive, UserActive = m.User!.IsActive })
            .FirstOrDefaultAsync(cancellationToken);
        if (membership is null)
        {
            return null;
        }

        var roles = await db.Set<MembershipRole>()
            .AsNoTracking()
            .Where(r => r.MembershipId == membership.Id)
            .Select(r => new { r.Role!.Name, Permissions = r.Role.Permissions.Select(p => p.Permission).ToList() })
            .ToListAsync(cancellationToken);

        var snapshot = new AccessSnapshot(
            membership.Id,
            membership.IsActive,
            membership.UserActive,
            [.. roles.Select(r => r.Name).Order(StringComparer.Ordinal)],
            [.. roles.SelectMany(r => r.Permissions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
        await SetAsync(key, snapshot, AccessTtl, cancellationToken);
        return snapshot;
    }

    private Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken) =>
        cache.SetAsync(
            key,
            JsonSerializer.SerializeToUtf8Bytes(value),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken);

    // Every key carries the tenant id, so an entry can never be served to another tenant.
    private static string AccessKey(Guid tenantId, Guid userId) => $"solvix:tenant:{tenantId}:access:{userId}";

    private static string SessionKey(Guid tenantId, Guid sessionId) => $"solvix:tenant:{tenantId}:session:{sessionId}";

    private static string PolicyKey(Guid tenantId) => $"solvix:tenant:{tenantId}:security-policy";

    private sealed record SessionSnapshot(Guid UserId, DateTimeOffset ExpiresAtUtc, DateTimeOffset IdleExpiresAtUtc, DateTimeOffset? RevokedAtUtc, bool MfaVerified);

    private sealed record AccessSnapshot(Guid MembershipId, bool MembershipActive, bool UserActive, string[] Roles, string[] Permissions);
}
