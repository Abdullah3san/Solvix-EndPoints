namespace Solvix.Modules.Support.Application;

using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.EntityFrameworkCore;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Security;
using Solvix.Modules.Support.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// The latest frame of each shared screen. Frames are ephemeral: only the newest is kept (viewers
/// that fall behind simply skip ahead) and it expires within seconds. Backed by the distributed
/// cache so any API instance can serve a viewer.
/// </summary>
public interface IScreenFrameStore
{
    Task StoreAsync(Guid tenantId, Guid sessionId, int width, int height, byte[] jpeg, CancellationToken cancellationToken);

    Task<StoredFrame?> GetAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken);
}

public sealed record StoredFrame(long Sequence, int Width, int Height, byte[] Jpeg);

internal sealed class DistributedScreenFrameStore(IDistributedCache cache) : IScreenFrameStore
{
    private const int HeaderLength = 16;
    private static readonly DistributedCacheEntryOptions Lifetime = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) };

    public Task StoreAsync(Guid tenantId, Guid sessionId, int width, int height, byte[] jpeg, CancellationToken cancellationToken)
    {
        var value = new byte[HeaderLength + jpeg.Length];
        BinaryPrimitives.WriteInt64BigEndian(value, DateTime.UtcNow.Ticks); // strictly increasing from the single writer (the agent)
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(8), width);
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(12), height);
        jpeg.CopyTo(value, HeaderLength);
        return cache.SetAsync(Key(tenantId, sessionId), value, Lifetime, cancellationToken);
    }

    public async Task<StoredFrame?> GetAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        var value = await cache.GetAsync(Key(tenantId, sessionId), cancellationToken);
        if (value is null || value.Length <= HeaderLength)
        {
            return null;
        }

        return new StoredFrame(
            BinaryPrimitives.ReadInt64BigEndian(value),
            BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(8)),
            BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(12)),
            value[HeaderLength..]);
    }

    // The tenant is part of the key so a session id from another tenant can never address a frame.
    private static string Key(Guid tenantId, Guid sessionId) => $"screen:{tenantId:N}:{sessionId:N}";
}

/// <summary>Technician-facing viewer for a screen session.</summary>
public interface IRemoteScreenService
{
    /// <summary>Waits (long poll) for a frame newer than <paramref name="after"/>; returns only the state when none arrives.</summary>
    Task<ScreenFrameResult> GetFrameAsync(Guid sessionId, long after, int waitSeconds, CancellationToken cancellationToken);
}

internal sealed class RemoteScreenService(ISolvixDbContext db, ITenantContext tenantContext, IScreenFrameStore frames, IClock clock) : IRemoteScreenService
{
    public const int MaxWaitSeconds = 25;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public async Task<ScreenFrameResult> GetFrameAsync(Guid sessionId, long after, int waitSeconds, CancellationToken cancellationToken)
    {
        var session = await db.Set<RemoteSession>().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new NotFoundException("Remote session", sessionId);
        if (session.Kind != RemoteSessionKind.Screen)
        {
            throw new ConflictException("This is not a screen session.", "remote_wrong_kind");
        }

        if (session.ApplyTimeouts(clock.UtcNow))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var tenantId = tenantContext.RequireTenantId();
        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
        var timer = Stopwatch.StartNew();
        var status = session.Status;
        while (true)
        {
            var frame = status == RemoteSessionStatus.Active ? await frames.GetAsync(tenantId, sessionId, cancellationToken) : null;
            if (frame is not null && frame.Sequence > after)
            {
                return new ScreenFrameResult(frame.Sequence, frame.Width, frame.Height, frame.Jpeg, status);
            }

            if (timer.Elapsed >= wait || status is not (RemoteSessionStatus.Pending or RemoteSessionStatus.Active))
            {
                return new ScreenFrameResult(after, 0, 0, null, status);
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new ScreenFrameResult(after, 0, 0, null, status);
            }

            // Re-read the state (untracked) so an ended session stops the poll promptly.
            status = await db.Set<RemoteSession>().AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.Status).FirstAsync(cancellationToken);
        }
    }
}

/// <summary>Agent side: accept a frame of the screen it is sharing.</summary>
public interface IAgentScreenService
{
    Task PostFrameAsync(Guid sessionId, int width, int height, byte[] jpeg, CancellationToken cancellationToken);
}

internal sealed class AgentScreenService(ISolvixDbContext db, ITenantContext tenantContext, ICurrentDevice currentDevice, IScreenFrameStore frames, IClock clock) : IAgentScreenService
{
    public async Task PostFrameAsync(Guid sessionId, int width, int height, byte[] jpeg, CancellationToken cancellationToken)
    {
        if (jpeg.Length is 0 or > ScreenLimits.MaxFrameBytes
            || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg[2] != 0xFF
            || width is < 1 or > 16384 || height is < 1 or > 16384)
        {
            throw new ValidationException("frame", "Not a valid JPEG frame.");
        }

        var session = await db.Set<RemoteSession>().FirstOrDefaultAsync(s => s.Id == sessionId && s.DeviceId == currentDevice.DeviceId, cancellationToken)
            ?? throw new NotFoundException("Remote session", sessionId);
        if (session.ApplyTimeouts(clock.UtcNow))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        if (session.Kind != RemoteSessionKind.Screen || session.Status != RemoteSessionStatus.Active)
        {
            throw new ConflictException("The session is not an active screen session.", "remote_session_not_active");
        }

        await frames.StoreAsync(tenantContext.RequireTenantId(), sessionId, width, height, jpeg, cancellationToken);
        session.Touch(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }
}
