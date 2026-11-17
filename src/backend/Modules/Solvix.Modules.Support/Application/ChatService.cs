namespace Solvix.Modules.Support.Application;

using Microsoft.EntityFrameworkCore;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Support.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>Technician-facing chat with the person using a device.</summary>
public interface IChatService
{
    Task<IReadOnlyList<ChatConversationDto>> ListConversationsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatMessageDto>> ListAsync(Guid deviceId, long after, CancellationToken cancellationToken);

    Task<ChatMessageDto> SendAsync(Guid deviceId, SendChatRequest request, CancellationToken cancellationToken);
}

internal sealed class ChatService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IMemberDirectory members) : IChatService
{
    public const int PageSize = 200;

    public async Task<IReadOnlyList<ChatConversationDto>> ListConversationsAsync(CancellationToken cancellationToken)
    {
        var latestSequences = db.Set<DeviceChatMessage>()
            .GroupBy(m => m.DeviceId)
            .Select(g => g.Max(m => m.Sequence))
            .OrderByDescending(sequence => sequence)
            .Take(100);
        var latest = await db.Set<DeviceChatMessage>().AsNoTracking()
            .Where(m => latestSequences.Contains(m.Sequence))
            .OrderByDescending(m => m.Sequence)
            .ToListAsync(cancellationToken);
        var ids = latest.Select(m => m.DeviceId).ToList();
        var names = await db.Set<Device>().AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .Select(d => new { d.Id, Name = d.DisplayName ?? d.Hostname })
            .ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);
        return latest
            .Select(m => new ChatConversationDto(m.DeviceId, names.GetValueOrDefault(m.DeviceId, "Unknown device"), m.Body, m.Sender, m.CreatedAtUtc, m.Sequence))
            .ToList();
    }

    public async Task<IReadOnlyList<ChatMessageDto>> ListAsync(Guid deviceId, long after, CancellationToken cancellationToken)
    {
        await EnsureDeviceAsync(deviceId, cancellationToken);

        // `after` = 0 returns the most recent page, in order; otherwise the messages that follow it.
        if (after <= 0)
        {
            var recent = await db.Set<DeviceChatMessage>().AsNoTracking()
                .Where(m => m.DeviceId == deviceId)
                .OrderByDescending(m => m.Sequence)
                .Take(PageSize)
                .ToListAsync(cancellationToken);
            recent.Reverse();
            return recent.Select(ToDto).ToList();
        }

        var next = await db.Set<DeviceChatMessage>().AsNoTracking()
            .Where(m => m.DeviceId == deviceId && m.Sequence > after)
            .OrderBy(m => m.Sequence)
            .Take(PageSize)
            .ToListAsync(cancellationToken);
        return next.Select(ToDto).ToList();
    }

    public async Task<ChatMessageDto> SendAsync(Guid deviceId, SendChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await EnsureDeviceAsync(deviceId, cancellationToken);
        if (device.Status != DeviceStatus.Active)
        {
            throw new ConflictException("The device is retired and cannot receive messages.", "device_retired");
        }

        string? name = currentUser.Email;
        if (currentUser.UserId is { } userId)
        {
            var found = await members.FindAsync([userId], cancellationToken);
            if (found.TryGetValue(userId, out var member))
            {
                name = member.DisplayName;
            }
        }

        var message = DeviceChatMessage.Create(tenantContext.RequireTenantId(), deviceId, ChatSender.Technician, currentUser.UserId, name, request.Body);
        db.Set<DeviceChatMessage>().Add(message);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(message);
    }

    internal static ChatMessageDto ToDto(DeviceChatMessage m) => new(m.Sequence, m.Sender, m.SenderName, m.Body, m.CreatedAtUtc);

    private async Task<Device> EnsureDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        await db.Set<Device>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
        ?? throw new NotFoundException("Device", deviceId);
}
