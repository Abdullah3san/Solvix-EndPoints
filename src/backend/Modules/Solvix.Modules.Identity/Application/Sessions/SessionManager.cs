namespace Solvix.Modules.Identity.Application.Sessions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Application.Access;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public sealed record IssuedSession(
    UserSession Session,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc);

public enum RefreshFailure
{
    None = 0,
    Invalid = 1,
    Expired = 2,
    ReuseDetected = 3,
    AccessLost = 4,
}

public sealed record RefreshOutcome(RefreshFailure Failure, IssuedSession? Issued, User? User, TenantDescriptor? Tenant, UserSession? Session)
{
    public static RefreshOutcome Failed(RefreshFailure failure, UserSession? session = null, User? user = null) =>
        new(failure, null, user, null, session);
}

/// <summary>
/// Creates, rotates and revokes sessions. All methods add to the caller's unit
/// of work; the caller saves. Sessions and refresh tokens are tenant-owned rows,
/// but sign-in and refresh happen before a tenant scope exists, so the queries
/// here lift the tenant filter explicitly and always pin the tenant themselves.
/// </summary>
public interface ISessionManager
{
    Task<IssuedSession> StartAsync(User user, TenantDescriptor tenant, bool mfaVerified, CancellationToken cancellationToken);

    /// <summary>Redeems a refresh token for a new access/refresh pair. Requires platform scope.</summary>
    Task<RefreshOutcome> RotateAsync(string? refreshToken, CancellationToken cancellationToken);

    /// <summary>Finds the session a refresh token belongs to without redeeming it. Requires platform scope.</summary>
    Task<UserSession?> FindByRefreshTokenAsync(string? refreshToken, CancellationToken cancellationToken);

    /// <summary>Revokes one session. Returns false when it was already revoked.</summary>
    Task<bool> RevokeAsync(UserSession session, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes every active session of a user, in one tenant or (with a null
    /// tenant, platform scope) everywhere. Returns the number revoked.
    /// </summary>
    Task<int> RevokeAllAsync(Guid userId, Guid? tenantId, Guid? exceptSessionId, string reason, CancellationToken cancellationToken);
}

internal sealed class SessionManager(
    ISolvixDbContext db,
    ITokenService tokens,
    ISecurityPolicyReader policies,
    IAccessCache accessCache,
    ITenantDirectory tenantDirectory,
    IClientInfo client,
    IClock clock,
    IOptions<IdentitySecurityOptions> options) : ISessionManager
{
    private readonly IdentitySecurityOptions _options = options.Value;

    public async Task<IssuedSession> StartAsync(User user, TenantDescriptor tenant, bool mfaVerified, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(tenant);
        var now = clock.UtcNow;
        var (idle, absolute) = await LifetimesAsync(tenant.Id, cancellationToken);
        var session = UserSession.Start(tenant.Id, user.Id, now, idle, absolute, mfaVerified, client.IpAddress, client.UserAgent);
        db.Set<UserSession>().Add(session);
        return Issue(user, tenant, session, parentTokenId: null);
    }

    public async Task<RefreshOutcome> RotateAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (!TokenGenerator.LooksLikeToken(refreshToken))
        {
            return RefreshOutcome.Failed(RefreshFailure.Invalid);
        }

        var now = clock.UtcNow;
        var hash = TokenGenerator.Hash(refreshToken!);
        var token = await db.Set<RefreshToken>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is null)
        {
            return RefreshOutcome.Failed(RefreshFailure.Invalid);
        }

        var session = await db.Set<UserSession>()
            .IgnoreQueryFilters()
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Id == token.SessionId && s.TenantId == token.TenantId, cancellationToken);
        if (session?.User is null)
        {
            return RefreshOutcome.Failed(RefreshFailure.Invalid);
        }

        var user = session.User;
        if (!session.IsActive(now) || now >= token.ExpiresAtUtc)
        {
            return RefreshOutcome.Failed(RefreshFailure.Expired, session, user);
        }

        if (token.ConsumedAtUtc is { } consumedAt)
        {
            // An already-rotated token. Within the grace window this is a second tab
            // racing the first; outside it, someone is replaying a stolen token.
            if (now - consumedAt > TimeSpan.FromSeconds(_options.RefreshTokenReuseGraceSeconds))
            {
                await RevokeAsync(session, SessionRevocationReasons.RefreshTokenReuse, cancellationToken);
                return RefreshOutcome.Failed(RefreshFailure.ReuseDetected, session, user);
            }
        }
        else
        {
            // Single winner: only one request can flip consumed_at_utc from NULL. A loser simply
            // continues as a grace-window sibling, which is what it is.
            await db.Set<RefreshToken>()
                .IgnoreQueryFilters()
                .Where(t => t.Id == token.Id && t.ConsumedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAtUtc, (DateTimeOffset?)now), cancellationToken);
        }

        // The session only stays alive while the user may still use the tenant.
        var tenant = await tenantDirectory.FindByIdAsync(session.TenantId, cancellationToken);
        var membershipActive = await db.Set<TenantMembership>()
            .IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == session.TenantId && m.UserId == user.Id && m.IsActive, cancellationToken);
        if (tenant is not { IsActive: true } || !membershipActive || !user.IsActive)
        {
            await RevokeAsync(session, SessionRevocationReasons.AccessLost, cancellationToken);
            return RefreshOutcome.Failed(RefreshFailure.AccessLost, session, user);
        }

        var (idle, _) = await LifetimesAsync(session.TenantId, cancellationToken);
        session.Touch(now, idle, client.IpAddress);
        var issued = Issue(user, tenant, session, token.Id);
        await accessCache.InvalidateSessionAsync(session.TenantId, session.Id, cancellationToken);
        return new RefreshOutcome(RefreshFailure.None, issued, user, tenant, session);
    }

    public async Task<UserSession?> FindByRefreshTokenAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (!TokenGenerator.LooksLikeToken(refreshToken))
        {
            return null;
        }

        var hash = TokenGenerator.Hash(refreshToken!);
        var token = await db.Set<RefreshToken>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        return token is null
            ? null
            : await db.Set<UserSession>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Id == token.SessionId && s.TenantId == token.TenantId, cancellationToken);
    }

    public async Task<bool> RevokeAsync(UserSession session, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var revoked = session.Revoke(clock.UtcNow, reason);
        await accessCache.InvalidateSessionAsync(session.TenantId, session.Id, cancellationToken);
        return revoked;
    }

    public async Task<int> RevokeAllAsync(Guid userId, Guid? tenantId, Guid? exceptSessionId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var query = db.Set<UserSession>().IgnoreQueryFilters().Where(s => s.UserId == userId && s.RevokedAtUtc == null && s.ExpiresAtUtc > now);
        if (tenantId is { } tenant)
        {
            query = query.Where(s => s.TenantId == tenant);
        }

        if (exceptSessionId is { } keep)
        {
            query = query.Where(s => s.Id != keep);
        }

        var sessions = await query.ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            await RevokeAsync(session, reason, cancellationToken);
        }

        return sessions.Count;
    }

    private IssuedSession Issue(User user, TenantDescriptor tenant, UserSession session, Guid? parentTokenId)
    {
        var refreshToken = TokenGenerator.NewToken();

        // The refresh token lives exactly as long as the idle window: not using the
        // session for that long ends it, and no token can outlive the session itself.
        var refreshExpires = session.IdleExpiresAtUtc;
        db.Set<RefreshToken>().Add(RefreshToken.Issue(session.TenantId, session.Id, TokenGenerator.Hash(refreshToken), refreshExpires, parentTokenId));
        var access = tokens.Issue(user, tenant, session);
        return new IssuedSession(session, access.AccessToken, access.ExpiresAtUtc, refreshToken, refreshExpires);
    }

    private async Task<(TimeSpan Idle, TimeSpan Absolute)> LifetimesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var policy = await policies.FindAsync(tenantId, cancellationToken);
        var idleMinutes = policy?.SessionIdleTimeoutMinutes ?? _options.Session.IdleTimeoutMinutes;
        var absoluteHours = policy?.SessionAbsoluteLifetimeHours ?? _options.Session.AbsoluteLifetimeHours;
        return (TimeSpan.FromMinutes(idleMinutes), TimeSpan.FromHours(absoluteHours));
    }
}
