namespace Solvix.Modules.Security.Application.Alerts;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Security.Application.Common;
using Solvix.Modules.Security.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public interface IAlertService
{
    Task<PagedResult<AlertDto>> ListAsync(AlertListQuery query, CancellationToken cancellationToken);

    Task<AlertDto> AcknowledgeAsync(Guid id, CancellationToken cancellationToken);

    Task<AlertDto> ResolveAsync(Guid id, NoteRequest request, CancellationToken cancellationToken);

    Task<AlertDto> DismissAsync(Guid id, NoteRequest request, CancellationToken cancellationToken);

    Task<PagedResult<SecurityEventDto>> EventsAsync(EventListQuery query, CancellationToken cancellationToken);
}

internal sealed class AlertService(ISolvixDbContext db, ICurrentUser user, IClock clock, IAuditTrail audit) : IAlertService
{
    public async Task<PagedResult<AlertDto>> ListAsync(AlertListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var alerts = db.Set<SecurityAlert>().AsNoTracking().AsQueryable();
        if (query.Status is { } status)
        {
            alerts = alerts.Where(a => a.Status == status);
        }
        else if (query.OpenOnly)
        {
            alerts = alerts.Where(a => a.Status == AlertStatus.New || a.Status == AlertStatus.Acknowledged);
        }

        if (query.MinSeverity is { } min)
        {
            var allowed = Enum.GetValues<Severity>().Where(s => s >= min).ToList();
            alerts = alerts.Where(a => allowed.Contains(a.Severity));
        }

        if (query.EndpointId is { } endpointId)
        {
            alerts = alerts.Where(a => a.EndpointId == endpointId);
        }

        var total = await alerts.LongCountAsync(cancellationToken);
        var rows = await alerts
            .OrderBy(a => a.Status == AlertStatus.New ? 0 : a.Status == AlertStatus.Acknowledged ? 1 : 2)
            .ThenByDescending(a => a.RaisedAtUtc)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        var hostnames = await HostnamesAsync(rows.Select(a => a.EndpointId), cancellationToken);
        return new PagedResult<AlertDto>(rows.Select(a => ToDto(a, hostnames)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<AlertDto> AcknowledgeAsync(Guid id, CancellationToken cancellationToken)
    {
        var alert = await FindAsync(id, cancellationToken);
        alert.Acknowledge(user.RequireUserId(), clock.UtcNow);
        audit.Record("alert.acknowledged", nameof(SecurityAlert), alert.Id, $"Acknowledged alert '{alert.Title}'.", new { alert.RuleKey, severity = alert.Severity.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(alert, await HostnamesAsync([alert.EndpointId], cancellationToken));
    }

    public async Task<AlertDto> ResolveAsync(Guid id, NoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var alert = await FindAsync(id, cancellationToken);
        alert.Resolve(user.RequireUserId(), request.Note, clock.UtcNow);
        audit.Record("alert.resolved", nameof(SecurityAlert), alert.Id, $"Resolved alert '{alert.Title}'.", new { alert.RuleKey, request.Note });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(alert, await HostnamesAsync([alert.EndpointId], cancellationToken));
    }

    public async Task<AlertDto> DismissAsync(Guid id, NoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var alert = await FindAsync(id, cancellationToken);
        alert.Dismiss(user.RequireUserId(), request.Note ?? string.Empty, clock.UtcNow);
        audit.Record("alert.dismissed", nameof(SecurityAlert), alert.Id, $"Dismissed alert '{alert.Title}' as not actionable.", new { alert.RuleKey, request.Note });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(alert, await HostnamesAsync([alert.EndpointId], cancellationToken));
    }

    public async Task<PagedResult<SecurityEventDto>> EventsAsync(EventListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var events = db.Set<SecurityEvent>().AsNoTracking().AsQueryable();
        if (query.EndpointId is { } endpointId)
        {
            events = events.Where(e => e.EndpointId == endpointId);
        }

        if (query.EventId is { } eventId)
        {
            events = events.Where(e => e.EventId == eventId);
        }

        if (query.From is { } from)
        {
            events = events.Where(e => e.TimeCreatedUtc >= from);
        }

        if (query.To is { } to)
        {
            events = events.Where(e => e.TimeCreatedUtc <= to);
        }

        var total = await events.LongCountAsync(cancellationToken);
        var rows = await events.OrderByDescending(e => e.TimeCreatedUtc).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        var hostnames = await HostnamesAsync(rows.Select(e => e.EndpointId), cancellationToken);
        return new PagedResult<SecurityEventDto>(
            rows.Select(e => new SecurityEventDto(e.Id, e.EndpointId, hostnames.GetValueOrDefault(e.EndpointId, string.Empty), e.Log, e.RecordId, e.EventId, e.Provider, e.TimeCreatedUtc, e.Summary, e.TargetGroup)).ToList(),
            page.SafePage, page.SafePageSize, total);
    }

    private async Task<SecurityAlert> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<SecurityAlert>().FirstOrDefaultAsync(a => a.Id == id, cancellationToken) ?? throw new NotFoundException("Alert", id);

    private async Task<Dictionary<Guid, string>> HostnamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var set = ids.Distinct().ToList();
        return await db.Set<ManagedEndpoint>().AsNoTracking().Where(e => set.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Hostname, cancellationToken);
    }

    private static AlertDto ToDto(SecurityAlert a, IReadOnlyDictionary<Guid, string> hostnames) => new(
        a.Id, a.EndpointId, hostnames.GetValueOrDefault(a.EndpointId, string.Empty), a.RuleKey, a.Severity, a.Status, a.Title, a.Evidence,
        a.FindingId, a.RaisedAtUtc, a.AcknowledgedAtUtc, a.ClosedAtUtc, a.ClosureNote, ExplanationDto.For(a.RuleKey));
}
