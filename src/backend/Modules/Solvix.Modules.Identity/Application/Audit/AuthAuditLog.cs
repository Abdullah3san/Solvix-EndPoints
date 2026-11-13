namespace Solvix.Modules.Identity.Application.Audit;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;

/// <summary>
/// Appends entries to the authentication audit trail. Entries are added to the
/// current unit of work, so an event is committed together with the change it
/// describes (or, on failure paths, by an explicit save before the error is raised).
/// </summary>
public interface IAuthAuditLog
{
    /// <summary>Records an event for one tenant, or for the platform when <paramref name="tenantId"/> is null.</summary>
    void Record(
        string eventType,
        AuditOutcome outcome,
        Guid? tenantId,
        Guid? userId = null,
        string? email = null,
        Guid? sessionId = null,
        string? detail = null,
        Guid? actorUserId = null);

    /// <summary>
    /// Records an event about an account in every tenant the user is an active
    /// member of, so each tenant's administrators see what happened to their
    /// member without seeing anything about other tenants. Falls back to a
    /// platform-level entry when the user has no memberships. Requires platform scope.
    /// </summary>
    Task RecordForUserAsync(
        string eventType,
        AuditOutcome outcome,
        User user,
        CancellationToken cancellationToken,
        Guid? sessionId = null,
        string? detail = null);
}

internal sealed class AuthAuditLog(ISolvixDbContext db, IClock clock, IClientInfo client) : IAuthAuditLog
{
    public void Record(
        string eventType,
        AuditOutcome outcome,
        Guid? tenantId,
        Guid? userId = null,
        string? email = null,
        Guid? sessionId = null,
        string? detail = null,
        Guid? actorUserId = null) =>
        db.Set<AuthAuditEvent>().Add(new AuthAuditEvent(
            clock.UtcNow,
            eventType,
            outcome,
            tenantId,
            userId,
            actorUserId,
            email,
            sessionId,
            client.IpAddress,
            client.UserAgent,
            client.CorrelationId,
            detail));

    public async Task RecordForUserAsync(
        string eventType,
        AuditOutcome outcome,
        User user,
        CancellationToken cancellationToken,
        Guid? sessionId = null,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenantIds = await db.Set<TenantMembership>()
            .IgnoreQueryFilters()
            .Where(m => m.UserId == user.Id && m.IsActive)
            .Select(m => m.TenantId)
            .ToListAsync(cancellationToken);

        if (tenantIds.Count == 0)
        {
            Record(eventType, outcome, tenantId: null, user.Id, user.Email, sessionId, detail);
            return;
        }

        foreach (var tenantId in tenantIds)
        {
            Record(eventType, outcome, tenantId, user.Id, user.Email, sessionId, detail);
        }
    }
}
