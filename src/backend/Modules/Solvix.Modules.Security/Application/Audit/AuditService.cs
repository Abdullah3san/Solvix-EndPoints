namespace Solvix.Modules.Security.Application.Audit;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Security.Domain;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public interface IAuditService
{
    Task<PagedResult<AuditEntryDto>> ListAsync(AuditListQuery query, CancellationToken cancellationToken);
}

internal sealed class AuditService(ISolvixDbContext db) : IAuditService
{
    public async Task<PagedResult<AuditEntryDto>> ListAsync(AuditListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var entries = db.Set<SecurityAuditEntry>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.Trim();
            entries = action.EndsWith('.') || !action.Contains('.', StringComparison.Ordinal)
                ? entries.Where(a => a.Action.StartsWith(action))
                : entries.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            var type = query.EntityType.Trim();
            entries = entries.Where(a => a.EntityType == type);
        }

        if (query.EntityId is { } entityId)
        {
            entries = entries.Where(a => a.EntityId == entityId);
        }

        if (query.ActorId is { } actorId)
        {
            entries = entries.Where(a => a.ActorId == actorId);
        }

        if (query.From is { } from)
        {
            entries = entries.Where(a => a.OccurredAtUtc >= from);
        }

        if (query.To is { } to)
        {
            entries = entries.Where(a => a.OccurredAtUtc <= to);
        }

        var total = await entries.LongCountAsync(cancellationToken);
        var rows = await entries.OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<AuditEntryDto>(rows.Select(AuditEntryDto.From).ToList(), page.SafePage, page.SafePageSize, total);
    }
}
