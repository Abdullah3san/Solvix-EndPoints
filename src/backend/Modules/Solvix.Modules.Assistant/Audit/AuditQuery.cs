namespace Solvix.Modules.Assistant.Audit;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Assistant.Domain;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public sealed record AuditEntryDto(
    Guid Id,
    string Kind,
    string Name,
    AuditStatus Status,
    string? UserEmail,
    Guid? ConversationId,
    Guid? ProposalId,
    string? Input,
    string? Summary,
    long DurationMs,
    string? CorrelationId,
    string? Model,
    DateTimeOffset CreatedAtUtc);

public sealed record AuditQuery(string? Kind, Guid? ProposalId, Guid? UserId, int Page = 1, int PageSize = 50);

public interface IAssistantAuditQuery
{
    Task<PagedResult<AuditEntryDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken);
}

internal sealed class AssistantAuditQuery(ISolvixDbContext db) : IAssistantAuditQuery
{
    public async Task<PagedResult<AuditEntryDto>> ListAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var entries = db.Set<AssistantAuditEntry>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            var kind = query.Kind.Trim();
            entries = entries.Where(e => e.Kind == kind || e.Kind.StartsWith(kind + "."));
        }

        if (query.ProposalId is { } proposalId)
        {
            entries = entries.Where(e => e.ProposalId == proposalId);
        }

        if (query.UserId is { } userId)
        {
            entries = entries.Where(e => e.UserId == userId);
        }

        var total = await entries.LongCountAsync(cancellationToken);
        var items = await entries.OrderByDescending(e => e.Id).Skip(page.Skip).Take(page.SafePageSize)
            .Select(e => new AuditEntryDto(e.Id, e.Kind, e.Name, e.Status, e.UserEmail, e.ConversationId, e.ProposalId, e.InputJson, e.Summary, e.DurationMs, e.CorrelationId, e.Model, e.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return new PagedResult<AuditEntryDto>(items, page.SafePage, page.SafePageSize, total);
    }
}
