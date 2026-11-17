namespace Solvix.Modules.Support.Application;

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Security;
using Solvix.Modules.Support.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>What an authenticated agent may do for the person using the computer: tickets and chat.</summary>
public interface IAgentSupportService
{
    Task<AgentTicket> SubmitTicketAsync(SubmitTicketRequest request, CancellationToken cancellationToken);

    Task<AgentTicketList> ListTicketsAsync(CancellationToken cancellationToken);

    Task<ChatMessageList> PollChatAsync(long after, int waitSeconds, CancellationToken cancellationToken);

    Task<ChatMessage> SendChatAsync(SendChatMessageRequest request, CancellationToken cancellationToken);
}

internal sealed class AgentSupportService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentDevice currentDevice,
    IClock clock) : IAgentSupportService
{
    public const int MaxTicketsPerWindow = 5;
    public static readonly TimeSpan TicketWindow = TimeSpan.FromMinutes(10);
    public const int MaxWaitSeconds = 25;
    public const int MaxPageSize = 100;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task<AgentTicket> SubmitTicketAsync(SubmitTicketRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await LoadDeviceAsync(cancellationToken);

        // A misbehaving script on one laptop must not flood the technicians' queue.
        var since = clock.UtcNow - TicketWindow;
        var recent = await db.Set<Ticket>().CountAsync(t => t.DeviceId == device.Id && t.Source == TicketSource.Agent && t.CreatedAtUtc >= since, cancellationToken);
        if (recent >= MaxTicketsPerWindow)
        {
            throw new ConflictException("Too many tickets were submitted from this device recently. Try again later.", "ticket_rate_limited");
        }

        var ticket = Ticket.Create(
            tenantContext.RequireTenantId(),
            TicketSource.Agent,
            device.Id,
            request.Subject,
            request.Description,
            request.Priority,
            request.RequesterName,
            request.RequesterEmail);
        db.Set<Ticket>().Add(ticket);
        await db.SaveChangesAsync(cancellationToken);
        return ToAgentTicket(ticket);
    }

    public async Task<AgentTicketList> ListTicketsAsync(CancellationToken cancellationToken)
    {
        var deviceId = currentDevice.DeviceId;
        var tickets = await db.Set<Ticket>().AsNoTracking()
            .Where(t => t.DeviceId == deviceId)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(MaxPageSize)
            .ToListAsync(cancellationToken);
        return new AgentTicketList { Tickets = tickets.Select(ToAgentTicket).ToList() };
    }

    public async Task<ChatMessageList> PollChatAsync(long after, int waitSeconds, CancellationToken cancellationToken)
    {
        var deviceId = currentDevice.DeviceId;
        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var messages = await Load(deviceId, after, cancellationToken);
            if (messages.Count > 0 || clock.Elapsed >= wait)
            {
                return new ChatMessageList { Messages = messages };
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new ChatMessageList();
            }
        }
    }

    public async Task<ChatMessage> SendChatAsync(SendChatMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await LoadDeviceAsync(cancellationToken);
        var message = DeviceChatMessage.Create(tenantContext.RequireTenantId(), device.Id, ChatSender.User, null, request.SenderName, request.Body);
        db.Set<DeviceChatMessage>().Add(message);
        await db.SaveChangesAsync(cancellationToken);
        return ToChat(message);
    }

    private async Task<List<ChatMessage>> Load(Guid deviceId, long after, CancellationToken cancellationToken)
    {
        List<DeviceChatMessage> found;
        if (after <= 0)
        {
            found = await db.Set<DeviceChatMessage>().AsNoTracking()
                .Where(m => m.DeviceId == deviceId)
                .OrderByDescending(m => m.Sequence)
                .Take(50)
                .ToListAsync(cancellationToken);
            found.Reverse();
        }
        else
        {
            found = await db.Set<DeviceChatMessage>().AsNoTracking()
                .Where(m => m.DeviceId == deviceId && m.Sequence > after)
                .OrderBy(m => m.Sequence)
                .Take(MaxPageSize)
                .ToListAsync(cancellationToken);
        }

        return found.Select(ToChat).ToList();
    }

    private async Task<Device> LoadDeviceAsync(CancellationToken cancellationToken) =>
        await db.Set<Device>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == currentDevice.DeviceId, cancellationToken)
        ?? throw new UnauthorizedException("Unknown device.");

    private static ChatMessage ToChat(DeviceChatMessage m) =>
        new() { Sequence = m.Sequence, Sender = m.Sender, SenderName = m.SenderName, Body = m.Body, SentAtUtc = m.CreatedAtUtc };

    private static AgentTicket ToAgentTicket(Ticket t) => new()
    {
        Id = t.Id,
        Number = t.Number,
        Subject = t.Subject,
        Status = t.Status,
        Priority = t.Priority,
        CreatedAtUtc = t.CreatedAtUtc,
        UpdatedAtUtc = t.UpdatedAtUtc,
    };
}
