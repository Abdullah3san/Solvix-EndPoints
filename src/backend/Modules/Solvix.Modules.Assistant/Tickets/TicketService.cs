namespace Solvix.Modules.Assistant.Tickets;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Assistant.Audit;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Fleet;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public sealed record TicketDeviceDto(Guid Id, string Name);

public sealed record TicketDto(
    Guid Id,
    string Reference,
    int Number,
    string Title,
    string Description,
    TicketPriority Priority,
    string? Category,
    TicketStatus Status,
    IReadOnlyList<TicketDeviceDto> Devices,
    string? CreatedByEmail,
    bool CreatedViaAssistant,
    Guid? ConversationId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ResolvedAtUtc);

public sealed record CreateTicketRequest(string Title, string Description, TicketPriority Priority, string? Category, IReadOnlyList<string>? Devices);

public sealed record UpdateTicketStatusRequest(TicketStatus Status);

public sealed record TicketListQuery(TicketStatus? Status, string? Search, int Page = 1, int PageSize = 25);

public interface ITicketService
{
    Task<TicketDto> CreateAsync(CreateTicketRequest request, bool viaAssistant, Guid? conversationId, CancellationToken cancellationToken);

    Task<PagedResult<TicketDto>> ListAsync(TicketListQuery query, CancellationToken cancellationToken);

    Task<TicketDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<TicketDto> SetStatusAsync(Guid id, TicketStatus status, CancellationToken cancellationToken);
}

internal sealed class TicketService(
    ISolvixDbContext db,
    IAssistantPrincipalAccessor principals,
    IFleetQueryService fleet,
    IAssistantAuditLog audit,
    IClock clock) : ITicketService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TicketDto> CreateAsync(CreateTicketRequest request, bool viaAssistant, Guid? conversationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var principal = principals.Current;

        var devices = new List<TicketDeviceDto>();
        if (request.Devices is { Count: > 0 })
        {
            if (request.Devices.Count > 50)
            {
                throw new ValidationException("devices", "A ticket can reference at most 50 devices.");
            }

            var resolution = await fleet.ResolveAsync(principal.ToFleetAccess(), request.Devices, cancellationToken);
            if (resolution.NotFound.Count > 0 || resolution.Ambiguous.Count > 0)
            {
                throw new ValidationException("devices", $"Unresolved devices: {string.Join(", ", resolution.NotFound.Concat(resolution.Ambiguous))}.");
            }

            devices.AddRange(resolution.Devices.Select(d => new TicketDeviceDto(d.Id, d.Name)));
        }

        var now = clock.UtcNow;
        var number = await NextNumberAsync(principal.TenantId, now, cancellationToken);

        var ticket = new AssistantTicket(
            principal.TenantId,
            number,
            SecretRedactor.RedactText(request.Title),
            SecretRedactor.RedactText(request.Description),
            request.Priority,
            request.Category,
            JsonSerializer.Serialize(devices, Json),
            principal.UserId,
            principal.Email,
            viaAssistant,
            conversationId);
        db.Set<AssistantTicket>().Add(ticket);
        await db.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new AuditRecord(AuditKinds.TicketCreated, ticket.Reference, AuditStatus.Success, conversationId, Input: new { ticket.Priority, devices = devices.Select(d => d.Name) }, Summary: ticket.Title),
            cancellationToken);
        return ToDto(ticket);
    }

    public async Task<PagedResult<TicketDto>> ListAsync(TicketListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var tickets = db.Set<AssistantTicket>().AsNoTracking().AsQueryable();
        if (query.Status is { } status)
        {
            tickets = tickets.Where(t => t.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            tickets = tickets.Where(t => EF.Functions.ILike(t.Title, pattern) || EF.Functions.ILike(t.Description, pattern));
        }

        var total = await tickets.LongCountAsync(cancellationToken);
        var items = await tickets.OrderByDescending(t => t.Number).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<TicketDto>(items.Select(ToDto).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<TicketDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ToDto(await db.Set<AssistantTicket>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Ticket", id));

    public async Task<TicketDto> SetStatusAsync(Guid id, TicketStatus status, CancellationToken cancellationToken)
    {
        if (!principals.Current.HasAtLeast(TenantRole.Admin))
        {
            throw new ForbiddenException("Changing ticket status requires the Admin or Owner role.");
        }

        var ticket = await db.Set<AssistantTicket>().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("Ticket", id);
        ticket.SetStatus(status, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(ticket);
    }

    /// <summary>
    /// Atomic per-tenant counter (INSERT … ON CONFLICT DO UPDATE … RETURNING). Run on the context's own connection so the
    /// tenant session variable (RLS) is applied; it cannot go through <c>SqlQuery</c> because EF would wrap the DML in a subquery.
    /// </summary>
    private async Task<int> NextNumberAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                """
                INSERT INTO assistant_ticket_counters (id, tenant_id, last_number, created_at_utc, updated_at_utc)
                VALUES (@id, @tenant, 1, @now, @now)
                ON CONFLICT (tenant_id) DO UPDATE SET last_number = assistant_ticket_counters.last_number + 1, updated_at_utc = @now
                RETURNING last_number
                """;
            AddParameter(command, "id", Guid.CreateVersion7());
            AddParameter(command, "tenant", tenantId);
            AddParameter(command, "now", now);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static TicketDto ToDto(AssistantTicket t) => new(
        t.Id, t.Reference, t.Number, t.Title, t.Description, t.Priority, t.Category, t.Status,
        JsonSerializer.Deserialize<List<TicketDeviceDto>>(t.DeviceRefsJson, Json) ?? [],
        t.CreatedByEmail, t.CreatedViaAssistant, t.ConversationId, t.CreatedAtUtc, t.ResolvedAtUtc);
}
