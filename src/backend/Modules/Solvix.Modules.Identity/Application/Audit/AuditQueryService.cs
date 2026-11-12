namespace Solvix.Modules.Identity.Application.Audit;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public sealed record AuditEventDto(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    AuditOutcome Outcome,
    Guid? UserId,
    Guid? ActorUserId,
    string? Email,
    Guid? SessionId,
    string? IpAddress,
    string? UserAgent,
    string? CorrelationId,
    string? Detail);

public sealed record AuditQuery(
    int Page = 1,
    int PageSize = 50,
    Guid? UserId = null,
    string? EventType = null,
    AuditOutcome? Outcome = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Search = null);

public interface IAuditQueryService
{
    Task<PagedResult<AuditEventDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Read side of the audit trail for the CURRENT tenant. The query filter and
/// the Row-Level Security policy both restrict it to rows of this tenant;
/// platform-level rows (no tenant) are never visible here.
/// </summary>
internal sealed class AuditQueryService(ISolvixDbContext db) : IAuditQueryService
{
    public async Task<PagedResult<AuditEventDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var events = db.Set<AuthAuditEvent>().AsNoTracking();

        if (query.UserId is { } userId)
        {
            events = events.Where(e => e.UserId == userId || e.ActorUserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            // "login." matches the whole family; an exact name matches one event.
            var type = query.EventType.Trim();
            events = type.EndsWith('.') ? events.Where(e => e.EventType.StartsWith(type)) : events.Where(e => e.EventType == type);
        }

        if (query.Outcome is { } outcome)
        {
            events = events.Where(e => e.Outcome == outcome);
        }

        if (query.From is { } from)
        {
            events = events.Where(e => e.OccurredAtUtc >= from);
        }

        if (query.To is { } to)
        {
            events = events.Where(e => e.OccurredAtUtc <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            events = events.Where(e => (e.Email != null && e.Email.ToUpper().Contains(term)) || (e.IpAddress != null && e.IpAddress.Contains(term)));
        }

        var total = await events.LongCountAsync(cancellationToken);
        var items = await events
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.Id)
            .Skip(page.Skip)
            .Take(page.SafePageSize)
            .Select(e => new AuditEventDto(
                e.Id, e.OccurredAtUtc, e.EventType, e.Outcome, e.UserId, e.ActorUserId, e.Email,
                e.SessionId, e.IpAddress, e.UserAgent, e.CorrelationId, e.Detail))
            .ToListAsync(cancellationToken);
        return new PagedResult<AuditEventDto>(items, page.SafePage, page.SafePageSize, total);
    }
}
