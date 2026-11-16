namespace Solvix.Modules.Helpdesk.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Helpdesk.Configuration;
using Solvix.Modules.Helpdesk.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Telemetry;
using Solvix.SharedKernel.Tenancy;

public interface ITicketService
{
    Task<PagedResult<TicketSummaryDto>> ListAsync(TicketListQuery query, CancellationToken cancellationToken);

    Task<TicketStatsDto> StatsAsync(CancellationToken cancellationToken);

    Task<TicketDetailsDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<TicketDetailsDto> CreateAsync(CreateTicketRequest request, CancellationToken cancellationToken);

    Task<TicketDetailsDto> UpdateAsync(Guid id, UpdateTicketRequest request, CancellationToken cancellationToken);

    Task<TicketDetailsDto> ChangeStatusAsync(Guid id, ChangeStatusRequest request, CancellationToken cancellationToken);

    Task<TicketDetailsDto> AssignAsync(Guid id, AssignTicketRequest request, CancellationToken cancellationToken);

    Task<TicketDetailsDto> CategorizeAsync(Guid id, CategorizeTicketRequest request, CancellationToken cancellationToken);

    Task<TicketDetailsDto> ChangePriorityAsync(Guid id, ChangePriorityRequest request, CancellationToken cancellationToken);

    Task<TicketDetailsDto> LinkDeviceAsync(Guid id, LinkDeviceRequest request, CancellationToken cancellationToken);

    Task<TicketDetailsDto> AddCommentAsync(Guid id, AddCommentRequest request, CancellationToken cancellationToken);

    Task<AttachmentDto> AddAttachmentAsync(Guid id, string? fileName, byte[] data, CancellationToken cancellationToken);

    Task<(AttachmentDto Meta, byte[] Data)> GetAttachmentAsync(Guid id, Guid attachmentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TicketEventDto>> HistoryAsync(Guid id, CancellationToken cancellationToken);

    Task<TicketDetailsDto> GenerateSummaryAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class TicketService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    HelpdeskActor actor,
    TicketFactory factory,
    IMemberDirectory members,
    IDeviceDirectory devices,
    ITicketSummarizer summarizer,
    IOptions<HelpdeskOptions> options,
    IClock clock) : ITicketService
{
    public async Task<PagedResult<TicketSummaryDto>> ListAsync(TicketListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var now = clock.UtcNow;
        var staff = await actor.IsStaffAsync(cancellationToken);
        var tickets = db.Set<Ticket>().AsNoTracking();

        if (!staff)
        {
            // Non-staff members only ever see tickets they raised. Applied before any other filter.
            var me = actor.UserId;
            tickets = tickets.Where(t => t.RequesterUserId == me);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var pattern = $"%{term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            var number = long.TryParse(term.TrimStart('#'), out var n) ? n : (long?)null;
            tickets = tickets.Where(t => EF.Functions.ILike(t.Subject, pattern) || (t.RequesterEmail != null && EF.Functions.ILike(t.RequesterEmail, pattern)) || t.Number == number);
        }

        if (query.Status is { } status)
        {
            tickets = tickets.Where(t => t.Status == status);
        }

        if (query.Open == true)
        {
            tickets = tickets.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);
        }

        if (query.Priority is { } priority)
        {
            tickets = tickets.Where(t => t.Priority == priority);
        }

        if (query.CategoryId is { } categoryId)
        {
            tickets = tickets.Where(t => t.CategoryId == categoryId);
        }

        if (query.AssigneeTechnicianId is { } assignee)
        {
            tickets = tickets.Where(t => t.AssigneeTechnicianId == assignee);
        }

        if (query.Mine == true && staff)
        {
            var mine = await actor.TechnicianIdAsync(cancellationToken);
            tickets = tickets.Where(t => t.AssigneeTechnicianId == mine);
        }

        if (query.Triage == true)
        {
            tickets = tickets.Where(t => t.CategoryId == null && t.Status != TicketStatus.Closed && t.Status != TicketStatus.Resolved);
        }

        if (query.Unassigned == true)
        {
            tickets = tickets.Where(t => t.AssigneeTechnicianId == null && t.Status != TicketStatus.Closed && t.Status != TicketStatus.Resolved);
        }

        if (query.Escalated == true)
        {
            tickets = tickets.Where(t => t.EscalationLevel > 0 && t.Status != TicketStatus.Closed && t.Status != TicketStatus.Resolved);
        }

        if (query.Overdue == true)
        {
            tickets = tickets.Where(t => t.Status != TicketStatus.Closed && t.Status != TicketStatus.Resolved && t.SlaPausedAtUtc == null
                && ((t.ResolutionDueAtUtc != null && t.ResolutionDueAtUtc < now) || (t.FirstRespondedAtUtc == null && t.FirstResponseDueAtUtc != null && t.FirstResponseDueAtUtc < now)));
        }

        var total = await tickets.LongCountAsync(cancellationToken);
        // Priority is persisted as text, so sorting by the column would be alphabetical: rank by severity explicitly.
        var rows = await tickets
            .OrderByDescending(t => t.Priority == TicketPriority.Critical ? 4 : t.Priority == TicketPriority.High ? 3 : t.Priority == TicketPriority.Medium ? 2 : 1)
            .ThenByDescending(t => t.LastActivityAtUtc)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<TicketSummaryDto>(await ToSummariesAsync(rows, staff, now, cancellationToken), page.SafePage, page.SafePageSize, total);
    }

    public async Task<TicketStatsDto> StatsAsync(CancellationToken cancellationToken)
    {
        await actor.RequireStaffAsync(cancellationToken);
        var now = clock.UtcNow;
        var soon = now.AddHours(4);
        var open = db.Set<Ticket>().AsNoTracking().Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);
        return new TicketStatsDto(
            await open.CountAsync(cancellationToken),
            await open.CountAsync(t => t.AssigneeTechnicianId == null, cancellationToken),
            await open.CountAsync(t => t.CategoryId == null, cancellationToken),
            await open.CountAsync(t => t.EscalationLevel > 0, cancellationToken),
            await open.CountAsync(t => t.SlaPausedAtUtc == null && ((t.ResolutionDueAtUtc != null && t.ResolutionDueAtUtc < now) || (t.FirstRespondedAtUtc == null && t.FirstResponseDueAtUtc != null && t.FirstResponseDueAtUtc < now)), cancellationToken),
            await db.Set<Ticket>().AsNoTracking().CountAsync(t => t.ResolvedAtUtc != null && t.ResolvedAtUtc > now.AddDays(-7), cancellationToken),
            await open.CountAsync(t => t.SlaPausedAtUtc == null && t.ResolutionDueAtUtc != null && t.ResolutionDueAtUtc >= now && t.ResolutionDueAtUtc < soon, cancellationToken));
    }

    public async Task<TicketDetailsDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var ticket = await FindVisibleAsync(id, tracking: false, cancellationToken);
        return await ToDetailsAsync(ticket, cancellationToken);
    }

    public async Task<TicketDetailsDto> CreateAsync(CreateTicketRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenantContext.RequireTenantId();
        var staff = await actor.IsStaffAsync(cancellationToken);
        var me = (await members.FindAsync([actor.UserId!.Value], cancellationToken)).GetValueOrDefault(actor.UserId!.Value);

        // Only staff may file on behalf of someone else, pick a category, set priority or link a device freely.
        var onBehalf = staff && !string.IsNullOrWhiteSpace(request.RequesterEmail);
        var categoryId = request.CategoryId;
        if (categoryId is not null)
        {
            await EnsureCategoryAsync(categoryId.Value, cancellationToken);
        }

        if (request.DeviceId is { } deviceId)
        {
            await EnsureDeviceAsync(deviceId, cancellationToken);
        }

        var ticket = await factory.CreateAsync(
            tenantId,
            request.Subject!,
            request.Description!,
            staff ? request.Priority ?? TicketPriority.Medium : TicketPriority.Medium,
            TicketSource.Web,
            categoryId,
            onBehalf ? null : actor.UserId,
            onBehalf ? request.RequesterEmail : me?.Email,
            onBehalf ? request.RequesterName : me?.DisplayName,
            request.DeviceId,
            null,
            null,
            me?.DisplayName ?? "user",
            actor.UserId,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        SolvixMetrics.TicketsCreated.Add(1, new KeyValuePair<string, object?>("source", "web"));
        return await ToDetailsAsync(ticket, cancellationToken);
    }

    public async Task<TicketDetailsDto> UpdateAsync(Guid id, UpdateTicketRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ticket = await FindVisibleAsync(id, tracking: true, cancellationToken);
        var staff = await actor.IsStaffAsync(cancellationToken);
        if (!staff && ticket.Status != TicketStatus.New)
        {
            throw new ForbiddenException("A ticket can only be edited by its requester until a technician picks it up.");
        }

        ticket.UpdateContent(request.Subject!, request.Description!, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(ticket, cancellationToken);
    }

    public async Task<TicketDetailsDto> ChangeStatusAsync(Guid id, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ticket = await FindVisibleAsync(id, tracking: true, cancellationToken);
        var staff = await actor.IsStaffAsync(cancellationToken);
        var now = clock.UtcNow;
        var tenantId = ticket.TenantId;

        if (!staff)
        {
            // Requesters may reopen a resolved ticket or close one that was resolved; nothing else.
            var allowed = (ticket.Status == TicketStatus.Resolved && request.Status is TicketStatus.Open or TicketStatus.Closed)
                || (ticket.Status == TicketStatus.Closed && request.Status == TicketStatus.Open);
            if (!allowed)
            {
                throw new ForbiddenException("You can only reopen or close your own resolved tickets.");
            }
        }

        if (request.Status == TicketStatus.Resolved && string.IsNullOrWhiteSpace(request.Resolution))
        {
            throw new ValidationException("resolution", "Describe how the ticket was resolved.");
        }

        var policy = await PolicyAsync(ticket, cancellationToken);
        var previous = ticket.Status;
        ticket.ChangeStatus(request.Status, now, policy?.Calendar());

        var label = await ActorLabelAsync(cancellationToken);
        db.Set<TicketEvent>().Add(TicketEvent.Create(tenantId, ticket.Id, TicketEventType.StatusChanged, actor.UserId, label, previous.ToString(), request.Status.ToString(), null, now));

        if (request.Status == TicketStatus.Resolved)
        {
            db.Set<TicketResolution>().Add(TicketResolution.Create(tenantId, ticket.Id, actor.UserId, await actor.TechnicianIdAsync(cancellationToken), request.Resolution!, now));
            db.Set<TicketEvent>().Add(TicketEvent.Create(tenantId, ticket.Id, TicketEventType.Resolved, actor.UserId, label, null, null, request.Resolution, now));
        }
        else if (request.Status == TicketStatus.Closed)
        {
            db.Set<TicketEvent>().Add(TicketEvent.Create(tenantId, ticket.Id, TicketEventType.Closed, actor.UserId, label, null, null, null, now));
        }
        else if (previous is TicketStatus.Resolved or TicketStatus.Closed)
        {
            var open = await db.Set<TicketResolution>().Where(r => r.TicketId == ticket.Id && r.ReopenedAtUtc == null).ToListAsync(cancellationToken);
            foreach (var resolution in open)
            {
                resolution.MarkReopened(now);
            }

            db.Set<TicketEvent>().Add(TicketEvent.Create(tenantId, ticket.Id, TicketEventType.Reopened, actor.UserId, label, previous.ToString(), request.Status.ToString(), null, now));
        }

        await db.SaveChangesAsync(cancellationToken);

        if (request.Status == TicketStatus.Resolved)
        {
            await TrySummarizeAsync(ticket, cancellationToken);
        }

        return await ToDetailsAsync(ticket, cancellationToken);
    }

    public async Task<TicketDetailsDto> AssignAsync(Guid id, AssignTicketRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await actor.RequireStaffAsync(cancellationToken);
        var ticket = await FindVisibleAsync(id, tracking: true, cancellationToken);
        if (request.TechnicianId is { } technicianId
            && !await db.Set<Technician>().AnyAsync(t => t.Id == technicianId && t.IsActive, cancellationToken))
        {
            throw new NotFoundException("Technician", technicianId);
        }

        var previous = ticket.AssigneeTechnicianId;
        ticket.Assign(request.TechnicianId, clock.UtcNow);
        db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.Assigned, actor.UserId, await ActorLabelAsync(cancellationToken), previous?.ToString(), request.TechnicianId?.ToString(), null, clock.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(ticket, cancellationToken);
    }

    public async Task<TicketDetailsDto> CategorizeAsync(Guid id, CategorizeTicketRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await actor.RequireStaffAsync(cancellationToken);
        var ticket = await FindVisibleAsync(id, tracking: true, cancellationToken);
        if (request.CategoryId is { } categoryId)
        {
            await EnsureCategoryAsync(categoryId, cancellationToken);
        }

        var previous = ticket.CategoryId;
        var now = clock.UtcNow;
        ticket.SetCategory(request.CategoryId, now);
        db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.CategoryChanged, actor.UserId, await ActorLabelAsync(cancellationToken), previous?.ToString(), request.CategoryId?.ToString(), null, now));

        // Triage completes the intake: an unassigned ticket now follows the category's assignment rules.
        if (ticket.AssigneeTechnicianId is null && request.CategoryId is not null)
        {
            var picked = await new TechnicianAssigner(db).PickAsync(request.CategoryId, cancellationToken);
            if (picked is not null)
            {
                ticket.Assign(picked, now);
                db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.Assigned, null, "auto-assignment", null, picked.ToString(), "assigned by category rule", now));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(ticket, cancellationToken);
    }

    public async Task<TicketDetailsDto> ChangePriorityAsync(Guid id, ChangePriorityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await actor.RequireStaffAsync(cancellationToken);
        var ticket = await FindVisibleAsync(id, tracking: true, cancellationToken);
        var previous = ticket.Priority;
        var now = clock.UtcNow;
        ticket.SetPriority(request.Priority, now);
        var policy = await PolicyByIdAsync(ticket.SlaPolicyId, cancellationToken)
            ?? await db.Set<SlaPolicy>().Include(p => p.Targets).AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault && p.IsActive, cancellationToken);
        ticket.ApplySla(policy, now);
        db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.PriorityChanged, actor.UserId, await ActorLabelAsync(cancellationToken), previous.ToString(), request.Priority.ToString(), null, now));
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(ticket, cancellationToken);
    }

    public async Task<TicketDetailsDto> LinkDeviceAsync(Guid id, LinkDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ticket = await FindVisibleAsync(id, tracking: true, cancellationToken);
        if (request.DeviceId is { } deviceId)
        {
            await EnsureDeviceAsync(deviceId, cancellationToken);
        }

        ticket.LinkDevice(request.DeviceId, clock.UtcNow);
        db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.DeviceLinked, actor.UserId, await ActorLabelAsync(cancellationToken), null, request.DeviceId?.ToString(), null, clock.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(ticket, cancellationToken);
    }

    public async Task<TicketDetailsDto> AddCommentAsync(Guid id, AddCommentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ticket = await FindVisibleAsync(id, tracking: true, cancellationToken);
        var staff = await actor.IsStaffAsync(cancellationToken);
        if (request.IsInternal && !staff)
        {
            throw new ForbiddenException("Only technicians can write internal notes.");
        }

        if (ticket.Status == TicketStatus.Closed)
        {
            throw new ConflictException("The ticket is closed. Reopen it to add comments.", "ticket_closed");
        }

        var now = clock.UtcNow;
        var me = (await members.FindAsync([actor.UserId!.Value], cancellationToken)).GetValueOrDefault(actor.UserId!.Value);
        db.Set<TicketComment>().Add(TicketComment.Create(ticket.TenantId, ticket.Id, actor.UserId, me?.DisplayName, me?.Email, request.IsInternal, staff, request.Body!, TicketSource.Web, null));
        db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, request.IsInternal ? TicketEventType.InternalNote : TicketEventType.Commented, actor.UserId, me?.DisplayName, null, null, null, now));

        if (staff && !request.IsInternal)
        {
            ticket.RecordStaffResponse(now);
            if (ticket.Status == TicketStatus.New)
            {
                ticket.ChangeStatus(TicketStatus.Open, now, null);
            }
        }
        else if (!staff)
        {
            await ApplyRequesterReplyAsync(ticket, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(ticket, cancellationToken);
    }

    public async Task<AttachmentDto> AddAttachmentAsync(Guid id, string? fileName, byte[] data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        var ticket = await FindVisibleAsync(id, tracking: false, cancellationToken);
        var settings = options.Value.Attachments;
        if (data.Length > settings.MaxBytes)
        {
            throw new ValidationException("file", $"Files can be at most {settings.MaxBytes / (1024 * 1024)} MiB.");
        }

        if (await db.Set<TicketAttachment>().CountAsync(a => a.TicketId == id, cancellationToken) >= settings.MaxPerTicket)
        {
            throw new ConflictException("This ticket has reached its attachment limit.", "attachment_limit");
        }

        var (name, contentType, error) = AttachmentRules.Validate(fileName, data);
        if (error is not null)
        {
            throw new ValidationException("file", error);
        }

        var now = clock.UtcNow;
        var attachment = TicketAttachment.Create(ticket.TenantId, ticket.Id, null, name!, contentType!, data.Length, AttachmentRules.Sha256(data), actor.UserId);
        db.Set<TicketAttachment>().Add(attachment);
        db.Set<TicketAttachmentContent>().Add(TicketAttachmentContent.Create(ticket.TenantId, attachment.Id, data));
        db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.AttachmentAdded, actor.UserId, await ActorLabelAsync(cancellationToken), null, name, $"{data.Length} bytes", now));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(attachment);
    }

    public async Task<(AttachmentDto Meta, byte[] Data)> GetAttachmentAsync(Guid id, Guid attachmentId, CancellationToken cancellationToken)
    {
        await FindVisibleAsync(id, tracking: false, cancellationToken);
        var attachment = await db.Set<TicketAttachment>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == id, cancellationToken)
            ?? throw new NotFoundException("Attachment", attachmentId);
        var data = await db.Set<TicketAttachmentContent>().AsNoTracking().Where(c => c.AttachmentId == attachmentId).Select(c => c.Data).FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Attachment", attachmentId);
        return (ToDto(attachment), data);
    }

    public async Task<IReadOnlyList<TicketEventDto>> HistoryAsync(Guid id, CancellationToken cancellationToken)
    {
        await FindVisibleAsync(id, tracking: false, cancellationToken);
        var staff = await actor.IsStaffAsync(cancellationToken);
        var events = db.Set<TicketEvent>().AsNoTracking().Where(e => e.TicketId == id);
        if (!staff)
        {
            // Requesters see the public lifecycle, not internal notes or routing details.
            events = events.Where(e => e.Type != TicketEventType.InternalNote && e.Type != TicketEventType.Assigned && e.Type != TicketEventType.Escalated && e.Type != TicketEventType.SlaBreached && e.Type != TicketEventType.SummaryGenerated);
        }

        return await events.OrderBy(e => e.OccurredAtUtc)
            .Select(e => new TicketEventDto(e.Id, e.Type, e.ActorLabel, e.ActorUserId, e.FromValue, e.ToValue, e.Detail, e.OccurredAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<TicketDetailsDto> GenerateSummaryAsync(Guid id, CancellationToken cancellationToken)
    {
        await actor.RequireStaffAsync(cancellationToken);
        var ticket = await FindVisibleAsync(id, tracking: true, cancellationToken);
        var cooldown = TimeSpan.FromSeconds(options.Value.Ai.CooldownSeconds);
        if (ticket.SummaryGeneratedAtUtc is { } last && clock.UtcNow - last < cooldown)
        {
            throw new ConflictException("A summary was just generated. Try again in a moment.", "summary_cooldown");
        }

        await GenerateAsync(ticket, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(ticket, cancellationToken);
    }

    // ------------------------------------------------------------------------------------------------------------

    private async Task TrySummarizeAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        try
        {
            await GenerateAsync(ticket, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A summary is a convenience: resolving the ticket must never fail because of it.
        }
    }

    private async Task GenerateAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        var messages = await db.Set<TicketComment>().AsNoTracking()
            .Where(c => c.TicketId == ticket.Id && (!c.IsInternal || options.Value.Ai.IncludeInternalNotes))
            .OrderBy(c => c.CreatedAtUtc)
            .Take(60)
            .Select(c => new SummaryMessage(c.IsFromStaff, c.IsInternal, c.Body))
            .ToListAsync(cancellationToken);
        var resolutions = await db.Set<TicketResolution>().AsNoTracking().Where(r => r.TicketId == ticket.Id).OrderBy(r => r.ResolvedAtUtc).Select(r => r.Notes).ToListAsync(cancellationToken);
        var result = await summarizer.SummarizeAsync(
            new TicketSummaryInput(ticket.Number, ticket.Subject, ticket.Description, ticket.Status.ToString(), ticket.Priority.ToString(), messages, resolutions), cancellationToken);
        var now = clock.UtcNow;
        ticket.SetSummary(result.Text, result.Source, now);
        db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.SummaryGenerated, actor.UserId, result.Source, null, null, null, now));
        SolvixMetrics.AiSummaries.Add(1, new KeyValuePair<string, object?>("source", result.Source));
    }

    private async Task ApplyRequesterReplyAsync(Ticket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ticket.Status == TicketStatus.WaitingOnRequester)
        {
            ticket.ChangeStatus(TicketStatus.Open, now, (await PolicyAsync(ticket, cancellationToken))?.Calendar());
        }
        else if (ticket.Status == TicketStatus.Resolved)
        {
            ticket.ChangeStatus(TicketStatus.Open, now, null);
            var open = await db.Set<TicketResolution>().Where(r => r.TicketId == ticket.Id && r.ReopenedAtUtc == null).ToListAsync(cancellationToken);
            foreach (var r in open)
            {
                r.MarkReopened(now);
            }

            db.Set<TicketEvent>().Add(TicketEvent.Create(ticket.TenantId, ticket.Id, TicketEventType.Reopened, actor.UserId, "requester reply", TicketStatus.Resolved.ToString(), TicketStatus.Open.ToString(), null, now));
        }
    }

    private async Task<Ticket> FindVisibleAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.Set<Ticket>() : db.Set<Ticket>().AsNoTracking();
        var ticket = await query.FirstOrDefaultAsync(t => t.Id == id, cancellationToken) ?? throw new NotFoundException("Ticket", id);
        if (!await actor.IsStaffAsync(cancellationToken) && ticket.RequesterUserId != actor.UserId)
        {
            // Another member's ticket is indistinguishable from a missing one.
            throw new NotFoundException("Ticket", id);
        }

        return ticket;
    }

    private Task<SlaPolicy?> PolicyAsync(Ticket ticket, CancellationToken cancellationToken) => PolicyByIdAsync(ticket.SlaPolicyId, cancellationToken);

    private async Task<SlaPolicy?> PolicyByIdAsync(Guid? policyId, CancellationToken cancellationToken) =>
        policyId is null ? null : await db.Set<SlaPolicy>().Include(p => p.Targets).AsNoTracking().FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);

    private async Task EnsureCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        if (!await db.Set<TicketCategory>().AnyAsync(c => c.Id == categoryId && c.IsActive, cancellationToken))
        {
            throw new ValidationException("categoryId", "Unknown or inactive category.");
        }
    }

    private async Task EnsureDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        if (!(await devices.FindAsync([deviceId], cancellationToken)).ContainsKey(deviceId))
        {
            throw new ValidationException("deviceId", "Unknown device.");
        }
    }

    private async Task<string?> ActorLabelAsync(CancellationToken cancellationToken)
    {
        if (actor.UserId is not { } uid)
        {
            return null;
        }

        return (await members.FindAsync([uid], cancellationToken)).GetValueOrDefault(uid)?.DisplayName;
    }

    private async Task<IReadOnlyList<TicketSummaryDto>> ToSummariesAsync(IReadOnlyList<Ticket> rows, bool staff, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var categoryIds = rows.Where(t => t.CategoryId != null).Select(t => t.CategoryId!.Value).Distinct().ToList();
        var categories = await db.Set<TicketCategory>().AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var technicianIds = rows.Where(t => t.AssigneeTechnicianId != null).Select(t => t.AssigneeTechnicianId!.Value).Distinct().ToList();
        var technicians = await db.Set<Technician>().AsNoTracking().Where(t => technicianIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.UserId, cancellationToken);
        var names = await members.FindAsync(technicians.Values.Distinct().ToList(), cancellationToken);

        return rows.Select(t => new TicketSummaryDto(
            t.Id, t.Number, t.Subject, t.Status, t.Priority, t.Source,
            t.CategoryId, t.CategoryId is { } c && categories.TryGetValue(c, out var cn) ? cn : null,
            staff ? t.SuggestedCategoryId : null,
            t.NeedsTriage,
            t.RequesterName, t.RequesterEmail,
            t.AssigneeTechnicianId,
            t.AssigneeTechnicianId is { } a && technicians.TryGetValue(a, out var uid) && names.TryGetValue(uid, out var m) ? m.DisplayName : null,
            t.DeviceId,
            t.FirstResponseDueAtUtc, t.ResolutionDueAtUtc, t.FirstResponseBreached, t.ResolutionBreached,
            SlaStateOf(t, now), staff ? t.EscalationLevel : 0, t.CreatedAtUtc, t.LastActivityAtUtc)).ToList();
    }

    internal static string SlaStateOf(Ticket t, DateTimeOffset now)
    {
        if (t.ResolutionDueAtUtc is null && t.FirstResponseDueAtUtc is null)
        {
            return "none";
        }

        if (!t.IsOpen)
        {
            return t.ResolutionBreached || t.FirstResponseBreached ? "breached" : "met";
        }

        if (t.SlaPausedAtUtc is not null)
        {
            return "paused";
        }

        var overdue = (t.ResolutionDueAtUtc is { } r && now > r) || (t.FirstRespondedAtUtc is null && t.FirstResponseDueAtUtc is { } f && now > f);
        if (overdue || t.ResolutionBreached || t.FirstResponseBreached)
        {
            return "breached";
        }

        if (t.ResolutionDueAtUtc is { } due && due > t.CreatedAtUtc)
        {
            var elapsed = (now - t.CreatedAtUtc).TotalSeconds / (due - t.CreatedAtUtc).TotalSeconds;
            if (elapsed >= 0.75)
            {
                return "at_risk";
            }
        }

        return "ok";
    }

    private async Task<TicketDetailsDto> ToDetailsAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var staff = await actor.IsStaffAsync(cancellationToken);
        var summary = (await ToSummariesAsync([ticket], staff, now, cancellationToken))[0];

        var commentQuery = db.Set<TicketComment>().AsNoTracking().Where(c => c.TicketId == ticket.Id);
        if (!staff)
        {
            commentQuery = commentQuery.Where(c => !c.IsInternal);
        }

        var comments = await commentQuery.OrderBy(c => c.CreatedAtUtc)
            .Select(c => new CommentDto(c.Id, c.AuthorName, c.AuthorEmail, c.IsInternal, c.IsFromStaff, c.Body, c.Source, c.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        var attachments = (await db.Set<TicketAttachment>().AsNoTracking().Where(a => a.TicketId == ticket.Id).OrderBy(a => a.CreatedAtUtc).ToListAsync(cancellationToken)).Select(ToDto).ToList();
        var resolutions = await db.Set<TicketResolution>().AsNoTracking().Where(r => r.TicketId == ticket.Id).OrderBy(r => r.ResolvedAtUtc)
            .Select(r => new ResolutionDto(r.Id, r.Notes, staff ? r.ResolvedByTechnicianId : null, r.ResolvedAtUtc, r.ReopenedAtUtc))
            .ToListAsync(cancellationToken);

        string? deviceName = null;
        if (ticket.DeviceId is { } deviceId)
        {
            var found = await devices.FindAsync([deviceId], cancellationToken);
            deviceName = found.TryGetValue(deviceId, out var d) ? d.DisplayName ?? d.Hostname : null;
        }

        return new TicketDetailsDto(summary, ticket.Description, deviceName, ticket.Summary, ticket.SummaryGeneratedAtUtc, ticket.SummarySource, ticket.ReopenCount, comments, attachments, resolutions);
    }

    private static AttachmentDto ToDto(TicketAttachment a) => new(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.CreatedAtUtc);
}
