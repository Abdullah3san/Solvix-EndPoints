namespace Solvix.Modules.Support.Application;

using Microsoft.EntityFrameworkCore;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Support.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

/// <summary>Technician-facing ticket operations (web console).</summary>
public interface ITicketService
{
    Task<PagedResult<TicketSummaryDto>> ListAsync(TicketListQuery query, CancellationToken cancellationToken);

    Task<TicketDetailsDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<TicketDetailsDto> CreateAsync(CreateTicketRequest request, CancellationToken cancellationToken);

    Task<TicketDetailsDto> UpdateAsync(Guid id, UpdateTicketRequest request, CancellationToken cancellationToken);

    Task<TicketDetailsDto> AddCommentAsync(Guid id, AddTicketCommentRequest request, CancellationToken cancellationToken);
}

internal sealed class TicketService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IMemberDirectory members,
    IClock clock) : ITicketService
{
    public async Task<PagedResult<TicketSummaryDto>> ListAsync(TicketListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var tickets = db.Set<Ticket>().AsNoTracking();
        if (query.Status is { } status)
        {
            tickets = tickets.Where(t => t.Status == status);
        }

        if (query.Open == true)
        {
            tickets = tickets.Where(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress);
        }

        if (query.Priority is { } priority)
        {
            tickets = tickets.Where(t => t.Priority == priority);
        }

        if (query.DeviceId is { } deviceId)
        {
            tickets = tickets.Where(t => t.DeviceId == deviceId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            tickets = tickets.Where(t => EF.Functions.ILike(t.Subject, pattern) || (t.RequesterName != null && EF.Functions.ILike(t.RequesterName, pattern)));
        }

        var total = await tickets.LongCountAsync(cancellationToken);
        var items = await Summaries(tickets.OrderByDescending(t => t.CreatedAtUtc).Skip(page.Skip).Take(page.SafePageSize)).ToListAsync(cancellationToken);
        return new PagedResult<TicketSummaryDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<TicketDetailsDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var summary = await Summaries(db.Set<Ticket>().AsNoTracking().Where(t => t.Id == id)).FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Ticket", id);
        var extra = await db.Set<Ticket>().AsNoTracking().Where(t => t.Id == id)
            .Select(t => new { t.Description, t.RequesterEmail, t.ResolvedAtUtc })
            .FirstAsync(cancellationToken);
        var comments = await db.Set<TicketComment>().AsNoTracking()
            .Where(c => c.TicketId == id)
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => new TicketCommentDto(c.Id, c.Author, c.AuthorName, c.Body, c.IsInternal, c.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return new TicketDetailsDto
        {
            Summary = summary,
            Description = extra.Description,
            RequesterEmail = extra.RequesterEmail,
            ResolvedAtUtc = extra.ResolvedAtUtc,
            Comments = comments,
        };
    }

    public async Task<TicketDetailsDto> CreateAsync(CreateTicketRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeviceId is { } deviceId
            && !await db.Set<Device>().AnyAsync(d => d.Id == deviceId, cancellationToken))
        {
            throw new NotFoundException("Device", deviceId);
        }

        var name = await TechnicianNameAsync(cancellationToken);
        var ticket = Ticket.Create(
            tenantContext.RequireTenantId(),
            TicketSource.Web,
            request.DeviceId,
            request.Subject,
            request.Description,
            request.Priority,
            name,
            currentUser.Email);
        db.Set<Ticket>().Add(ticket);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(ticket.Id, cancellationToken);
    }

    public async Task<TicketDetailsDto> UpdateAsync(Guid id, UpdateTicketRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ticket = await FindAsync(id, cancellationToken);
        if (request.Status is { } status)
        {
            ticket.ChangeStatus(status, clock.UtcNow);
        }

        if (request.Priority is { } priority)
        {
            ticket.ChangePriority(priority);
        }

        if (request.Unassign)
        {
            ticket.Assign(null);
        }
        else if (request.AssignedUserId is { } userId)
        {
            var found = await members.FindAsync([userId], cancellationToken);
            if (!found.TryGetValue(userId, out var member))
            {
                throw new NotFoundException("Member", userId);
            }

            if (!member.IsActive)
            {
                throw new ValidationException("assignedUserId", "The user is not an active member of this workspace.");
            }

            ticket.Assign(userId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<TicketDetailsDto> AddCommentAsync(Guid id, AddTicketCommentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ticket = await FindAsync(id, cancellationToken);
        var name = await TechnicianNameAsync(cancellationToken);
        db.Set<TicketComment>().Add(TicketComment.Create(
            ticket.TenantId, id, CommentAuthor.Technician, currentUser.UserId, name, request.Body, request.IsInternal));
        ticket.UpdatedAtUtc = clock.UtcNow; // touch so the list shows recent activity
        if (ticket.Status == TicketStatus.Open && !request.IsInternal)
        {
            ticket.ChangeStatus(TicketStatus.InProgress, clock.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    private async Task<Ticket> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<Ticket>().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
        ?? throw new NotFoundException("Ticket", id);

    private async Task<string?> TechnicianNameAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return currentUser.Email;
        }

        var found = await members.FindAsync([userId], cancellationToken);
        return found.TryGetValue(userId, out var member) ? member.DisplayName : currentUser.Email;
    }

    private IQueryable<TicketSummaryDto> Summaries(IQueryable<Ticket> tickets) =>
        from t in tickets
        join d in db.Set<Device>().AsNoTracking() on t.DeviceId equals d.Id into devices
        from d in devices.DefaultIfEmpty()
        select new TicketSummaryDto(
            t.Id,
            t.Number,
            t.Subject,
            t.Status,
            t.Priority,
            t.Source,
            t.DeviceId,
            d == null ? null : (d.DisplayName ?? d.Hostname),
            t.RequesterName,
            t.AssignedUserId,
            t.CreatedAtUtc,
            t.UpdatedAtUtc);
}
