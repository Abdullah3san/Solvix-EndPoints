namespace Solvix.Modules.Software.Auditing;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Software.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public sealed record SoftwareAuditEntry(
    string EventType,
    SoftwareAuditOutcome Outcome,
    Guid? PackageId = null,
    Guid? DeploymentId = null,
    Guid? DeviceId = null,
    string? Detail = null,
    Guid? ActorUserId = null);

/// <summary>
/// Saves software management changes together with their audit events in one transaction under a
/// per-tenant advisory lock, so the chain is gap-free and a change is never committed without its event.
/// </summary>
public sealed class SoftwareAuditTrail(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IHttpContextAccessor httpContext,
    IClock clock)
{
    private readonly List<SoftwareAuditEntry> _pending = [];

    public void Record(SoftwareAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _pending.Add(entry);
    }

    public async Task<T> AtomicAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var tenantId = tenantContext.RequireTenantId();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async ct =>
            {
                _pending.Clear();

                // Every unit re-reads what it changes under the tenant lock: rows tracked by an earlier unit may
                // have been changed since by another request (e.g. an agent reporting a result).
                (db as DbContext)?.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var lockKey = $"solvix:software-audit:{tenantId:D}";
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
                var result = await action(ct);
                await AppendPendingAsync(tenantId, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            },
            cancellationToken);
    }

    public Task WriteAsync(SoftwareAuditEntry entry, CancellationToken cancellationToken) =>
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

        var last = await db.Set<SoftwareAuditEvent>().AsNoTracking()
            .OrderByDescending(e => e.Sequence)
            .Select(e => new { e.Sequence, e.Hash })
            .FirstOrDefaultAsync(cancellationToken);
        var sequence = last?.Sequence ?? 0;
        var previous = last?.Hash ?? SoftwareAuditEvent.GenesisHash;
        var now = clock.UtcNow;
        var ip = httpContext.HttpContext?.Connection.RemoteIpAddress?.ToString();
        foreach (var entry in _pending)
        {
            var row = new SoftwareAuditEvent(
                tenantId,
                ++sequence,
                now,
                entry.EventType,
                entry.Outcome,
                entry.ActorUserId ?? RequestUser(),
                entry.PackageId,
                entry.DeploymentId,
                entry.DeviceId,
                ip,
                entry.Detail is { Length: > 4000 } d ? d[..4000] : entry.Detail,
                previous);
            db.Set<SoftwareAuditEvent>().Add(row);
            previous = row.Hash;
        }

        _pending.Clear();
    }

    // Agent requests authenticate with sub = device id: a device is never recorded as a user.
    private Guid? RequestUser() =>
        currentUser.IsAuthenticated && httpContext.HttpContext?.User.FindFirst(SolvixClaimTypes.DeviceId) is null
            ? currentUser.UserId
            : null;
}

public sealed record SoftwareAuditEventDto(
    Guid Id,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    SoftwareAuditOutcome Outcome,
    Guid? ActorUserId,
    Guid? PackageId,
    Guid? DeploymentId,
    Guid? DeviceId,
    string? SourceIp,
    string? Detail,
    string Hash);

public sealed record SoftwareAuditQuery
{
    public Guid? PackageId { get; init; }

    public Guid? DeploymentId { get; init; }

    public Guid? DeviceId { get; init; }

    public string? EventType { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;
}

public sealed record SoftwareAuditVerification(bool Valid, long Checked, long? FirstInvalidSequence, string? Problem);

public interface ISoftwareAuditQueries
{
    Task<PagedResult<SoftwareAuditEventDto>> ListAsync(SoftwareAuditQuery query, CancellationToken cancellationToken);

    Task<SoftwareAuditVerification> VerifyAsync(CancellationToken cancellationToken);
}

internal sealed class SoftwareAuditQueries(ISolvixDbContext db) : ISoftwareAuditQueries
{
    public async Task<PagedResult<SoftwareAuditEventDto>> ListAsync(SoftwareAuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 200);
        var events = db.Set<SoftwareAuditEvent>().AsNoTracking();
        if (query.PackageId is { } packageId)
        {
            events = events.Where(e => e.PackageId == packageId);
        }

        if (query.DeploymentId is { } deploymentId)
        {
            events = events.Where(e => e.DeploymentId == deploymentId);
        }

        if (query.DeviceId is { } deviceId)
        {
            events = events.Where(e => e.DeviceId == deviceId);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            events = events.Where(e => e.EventType == query.EventType);
        }

        var total = await events.LongCountAsync(cancellationToken);
        var rows = await events.OrderByDescending(e => e.Sequence).Skip((page - 1) * size).Take(size)
            .Select(e => new SoftwareAuditEventDto(e.Id, e.Sequence, e.OccurredAtUtc, e.EventType, e.Outcome, e.ActorUserId, e.PackageId, e.DeploymentId, e.DeviceId, e.SourceIp, e.Detail, e.Hash))
            .ToListAsync(cancellationToken);
        return new PagedResult<SoftwareAuditEventDto>(rows, page, size, total);
    }

    public async Task<SoftwareAuditVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        const int batch = 1000;
        long expected = 1;
        var previous = SoftwareAuditEvent.GenesisHash;
        while (true)
        {
            var from = expected;
            var rows = await db.Set<SoftwareAuditEvent>().AsNoTracking().Where(e => e.Sequence >= from).OrderBy(e => e.Sequence).Take(batch).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                if (row.Sequence != expected)
                {
                    return new SoftwareAuditVerification(false, expected - 1, expected, "missing entry");
                }

                if (!string.Equals(row.PreviousHash, previous, StringComparison.Ordinal))
                {
                    return new SoftwareAuditVerification(false, expected - 1, row.Sequence, "broken link to the previous entry");
                }

                if (!string.Equals(row.ComputeHash(), row.Hash, StringComparison.Ordinal))
                {
                    return new SoftwareAuditVerification(false, expected - 1, row.Sequence, "content does not match its hash");
                }

                previous = row.Hash;
                expected++;
            }

            if (rows.Count < batch)
            {
                return new SoftwareAuditVerification(true, expected - 1, null, null);
            }
        }
    }
}
