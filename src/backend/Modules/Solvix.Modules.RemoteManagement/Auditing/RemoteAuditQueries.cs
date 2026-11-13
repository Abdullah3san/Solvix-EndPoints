namespace Solvix.Modules.RemoteManagement.Auditing;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.RemoteManagement.Domain;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public sealed record RemoteAuditEventDto(
    Guid Id,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    RemoteAuditOutcome Outcome,
    Guid? ActorUserId,
    Guid? DeviceId,
    Guid? ExecutionId,
    Guid? ApprovalId,
    string? CommandType,
    string? Permission,
    string? SourceIp,
    string? Detail,
    string Hash);

public sealed record RemoteAuditQuery
{
    public Guid? DeviceId { get; init; }

    public Guid? ExecutionId { get; init; }

    public Guid? ActorUserId { get; init; }

    public string? EventType { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;
}

/// <param name="FirstInvalidSequence">The first row whose hash or link to its predecessor does not match, if any.</param>
public sealed record AuditChainVerification(bool Valid, long Checked, long? FirstInvalidSequence, string? Problem);

public interface IRemoteAuditQueries
{
    Task<PagedResult<RemoteAuditEventDto>> ListAsync(RemoteAuditQuery query, CancellationToken cancellationToken);

    Task<AuditChainVerification> VerifyAsync(CancellationToken cancellationToken);
}

internal sealed class RemoteAuditQueries(ISolvixDbContext db) : IRemoteAuditQueries
{
    public async Task<PagedResult<RemoteAuditEventDto>> ListAsync(RemoteAuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 200);
        var events = db.Set<RemoteAuditEvent>().AsNoTracking();
        if (query.DeviceId is { } deviceId)
        {
            events = events.Where(e => e.DeviceId == deviceId);
        }

        if (query.ExecutionId is { } executionId)
        {
            events = events.Where(e => e.ExecutionId == executionId);
        }

        if (query.ActorUserId is { } actor)
        {
            events = events.Where(e => e.ActorUserId == actor);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            events = events.Where(e => e.EventType == query.EventType);
        }

        if (query.From is { } from)
        {
            events = events.Where(e => e.OccurredAtUtc >= from);
        }

        if (query.To is { } to)
        {
            events = events.Where(e => e.OccurredAtUtc < to);
        }

        var total = await events.LongCountAsync(cancellationToken);
        var rows = await events.OrderByDescending(e => e.Sequence).Skip((page - 1) * size).Take(size)
            .Select(e => new RemoteAuditEventDto(e.Id, e.Sequence, e.OccurredAtUtc, e.EventType, e.Outcome, e.ActorUserId, e.DeviceId, e.ExecutionId,
                e.ApprovalId, e.CommandType, e.Permission, e.SourceIp, e.Detail, e.Hash))
            .ToListAsync(cancellationToken);
        return new PagedResult<RemoteAuditEventDto>(rows, page, size, total);
    }

    /// <summary>Recomputes the tenant's chain: every hash, every link, no gaps in the sequence.</summary>
    public async Task<AuditChainVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        const int batch = 1000;
        long expectedSequence = 1;
        var previous = RemoteAuditEvent.GenesisHash;
        while (true)
        {
            var from = expectedSequence;
            var rows = await db.Set<RemoteAuditEvent>().AsNoTracking()
                .Where(e => e.Sequence >= from)
                .OrderBy(e => e.Sequence)
                .Take(batch)
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                if (row.Sequence != expectedSequence)
                {
                    return new AuditChainVerification(false, expectedSequence - 1, expectedSequence, "missing entry");
                }

                if (!string.Equals(row.PreviousHash, previous, StringComparison.Ordinal))
                {
                    return new AuditChainVerification(false, expectedSequence - 1, row.Sequence, "broken link to the previous entry");
                }

                if (!string.Equals(row.ComputeHash(), row.Hash, StringComparison.Ordinal))
                {
                    return new AuditChainVerification(false, expectedSequence - 1, row.Sequence, "content does not match its hash");
                }

                previous = row.Hash;
                expectedSequence++;
            }

            if (rows.Count < batch)
            {
                return new AuditChainVerification(true, expectedSequence - 1, null, null);
            }
        }
    }
}
