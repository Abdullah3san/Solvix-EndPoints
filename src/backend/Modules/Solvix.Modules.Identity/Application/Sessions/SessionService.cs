namespace Solvix.Modules.Identity.Application.Sessions;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface ISessionService
{
    /// <summary>The caller's own active sessions, in every workspace.</summary>
    Task<IReadOnlyList<SessionDto>> ListMineAsync(CancellationToken cancellationToken);

    Task RevokeMineAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<int> RevokeMyOtherSessionsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The caller's own sessions. These span the workspaces a person belongs to, so
/// the queries run in platform scope; they are always pinned to the user id
/// from the validated token, never to anything in the request.
/// </summary>
internal sealed class SessionService(
    ISolvixDbContext db,
    ISessionManager sessions,
    IAuthAuditLog audit,
    ITenantDirectory tenantDirectory,
    ITenantContextMutator tenantContextMutator,
    ICurrentUser currentUser,
    IClock clock) : ISessionService
{
    public async Task<IReadOnlyList<SessionDto>> ListMineAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var currentSessionId = currentUser.SessionId;
        var now = clock.UtcNow;
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var items = await db.Set<UserSession>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null && s.ExpiresAtUtc > now && s.IdleExpiresAtUtc > now)
            .OrderByDescending(s => s.LastSeenAtUtc)
            .ToListAsync(cancellationToken);

        var result = new List<SessionDto>(items.Count);
        foreach (var session in items)
        {
            var tenant = await tenantDirectory.FindByIdAsync(session.TenantId, cancellationToken);
            result.Add(new SessionDto(
                session.Id, session.TenantId, tenant?.Name ?? string.Empty, session.CreatedAtUtc, session.LastSeenAtUtc,
                session.ExpiresAtUtc, session.IpAddress, session.UserAgent, session.MfaVerified, session.Id == currentSessionId));
        }

        return result;
    }

    public async Task RevokeMineAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        // Someone else's session id is indistinguishable from one that does not exist.
        var session = await db.Set<UserSession>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken)
            ?? throw new NotFoundException("Session", sessionId);

        if (await sessions.RevokeAsync(session, SessionRevocationReasons.RevokedByUser, cancellationToken))
        {
            audit.Record(AuditEvents.SessionRevoked, AuditOutcome.Success, session.TenantId, userId, currentUser.Email, session.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> RevokeMyOtherSessionsAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var keep = currentUser.RequireSessionId();
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var user = await db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new UnauthorizedException();
        var revoked = await sessions.RevokeAllAsync(userId, tenantId: null, keep, SessionRevocationReasons.RevokedByUser, cancellationToken);
        await audit.RecordForUserAsync(AuditEvents.SessionRevoked, AuditOutcome.Success, user, cancellationToken, keep, $"other_sessions_revoked={revoked}");
        await db.SaveChangesAsync(cancellationToken);
        return revoked;
    }
}
