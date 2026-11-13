namespace Solvix.Modules.RemoteManagement.Auditing;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.RemoteManagement.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>What to record; the trail adds tenant, time, actor, address, sequence and hashes.</summary>
public sealed record RemoteAuditEntry(
    string EventType,
    RemoteAuditOutcome Outcome,
    Guid? DeviceId = null,
    Guid? ExecutionId = null,
    Guid? ApprovalId = null,
    string? CommandType = null,
    string? Permission = null,
    string? Detail = null,
    Guid? ActorUserId = null);

/// <summary>
/// Writes remote administration state changes and their audit events atomically. Every unit
/// of work for a tenant runs in one transaction under a per-tenant advisory lock, so the
/// audit chain is gap-free and strictly ordered, and a state change is never committed
/// without its audit event (or the other way round).
/// </summary>
public sealed class RemoteAuditTrail(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IHttpContextAccessor httpContext,
    IClock clock)
{
    private readonly List<RemoteAuditEntry> _pending = [];

    /// <summary>Queues an event for the current <see cref="AtomicAsync{T}"/> unit.</summary>
    public void Record(RemoteAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _pending.Add(entry);
    }

    /// <summary>Runs <paramref name="action"/> and saves its changes and the recorded events in one transaction.</summary>
    public async Task<T> AtomicAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var tenantId = tenantContext.RequireTenantId();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async ct =>
            {
                _pending.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var lockKey = $"solvix:remote-audit:{tenantId:D}";
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
                var result = await action(ct);
                await AppendPendingAsync(tenantId, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            },
            cancellationToken);
    }

    /// <summary>Records events that change nothing else (e.g. a denied request).</summary>
    public Task WriteAsync(RemoteAuditEntry entry, CancellationToken cancellationToken) =>
        AtomicAsync(
            _ =>
            {
                Record(entry);
                return Task.FromResult(true);
            },
            cancellationToken);

    private async Task AppendPendingAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var last = await db.Set<RemoteAuditEvent>()
            .AsNoTracking()
            .OrderByDescending(e => e.Sequence)
            .Select(e => new { e.Sequence, e.Hash })
            .FirstOrDefaultAsync(cancellationToken);
        var sequence = last?.Sequence ?? 0;
        var previous = last?.Hash ?? RemoteAuditEvent.GenesisHash;
        var now = clock.UtcNow;
        var ip = httpContext.HttpContext?.Connection.RemoteIpAddress?.ToString();
        foreach (var entry in _pending)
        {
            var row = new RemoteAuditEvent(
                tenantId,
                ++sequence,
                now,
                entry.EventType,
                entry.Outcome,
                entry.ActorUserId ?? RequestUser(),
                entry.DeviceId,
                entry.ExecutionId,
                entry.ApprovalId,
                entry.CommandType,
                entry.Permission,
                ip,
                Truncate(entry.Detail, 4000),
                previous);
            db.Set<RemoteAuditEvent>().Add(row);
            previous = row.Hash;
        }

        _pending.Clear();
    }

    // Agent requests authenticate with sub = device id: a device is never recorded as a user.
    private Guid? RequestUser() =>
        currentUser.IsAuthenticated && httpContext.HttpContext?.User.FindFirst(SolvixClaimTypes.DeviceId) is null
            ? currentUser.UserId
            : null;

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
