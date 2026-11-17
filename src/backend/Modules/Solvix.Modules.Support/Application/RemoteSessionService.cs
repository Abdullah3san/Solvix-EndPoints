namespace Solvix.Modules.Support.Application;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Support.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

/// <summary>Technician-facing remote command sessions (administrators only).</summary>
public interface IRemoteSessionService
{
    Task<RemoteSessionDto> StartAsync(Guid deviceId, StartRemoteSessionRequest request, CancellationToken cancellationToken);

    Task<PagedResult<RemoteSessionDto>> ListAsync(RemoteSessionListQuery query, CancellationToken cancellationToken);

    Task<RemoteSessionDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<RemoteOutputDto> OutputAsync(Guid id, long after, int waitSeconds, CancellationToken cancellationToken);

    Task SendInputAsync(Guid id, RemoteInputRequest request, CancellationToken cancellationToken);

    Task<RemoteSessionDto> EndAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed partial class RemoteSessionService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IMemberDirectory members,
    IClock clock,
    ILogger<RemoteSessionService> logger) : IRemoteSessionService
{
    public const int MaxWaitSeconds = 25;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly JsonSerializerOptions HealthJson = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static readonly TimeSpan ChunkRetention = TimeSpan.FromMinutes(5);

    public async Task<RemoteSessionDto> StartAsync(Guid deviceId, StartRemoteSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await db.Set<Device>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            ?? throw new NotFoundException("Device", deviceId);
        if (device.Status != DeviceStatus.Active)
        {
            throw new ConflictException("The device is retired.", "device_retired");
        }

        // The computer's own policy decides; the console only reports it.
        var mode = ReportedMode(device);
        if (mode == RemoteAccessMode.Disabled)
        {
            throw new ConflictException(
                "Remote access is not enabled on this computer. It is switched on locally by whoever installs the agent.",
                "remote_access_disabled");
        }

        var now = clock.UtcNow;
        await ReapAsync(await db.Set<RemoteSession>().Where(s => s.DeviceId == deviceId && (s.Status == RemoteSessionStatus.Pending || s.Status == RemoteSessionStatus.Active)).ToListAsync(cancellationToken), now, cancellationToken);
        if (await db.Set<RemoteSession>().AnyAsync(s => s.DeviceId == deviceId && (s.Status == RemoteSessionStatus.Pending || s.Status == RemoteSessionStatus.Active), cancellationToken))
        {
            throw new ConflictException("There is already an open remote session on this device.", "remote_session_open");
        }

        var cutoff = now - ChunkRetention;
        await db.Set<RemoteChunkRow>()
            .Where(c => db.Set<RemoteSession>().Any(s => s.Id == c.SessionId && s.EndedAtUtc != null && s.EndedAtUtc < cutoff))
            .ExecuteDeleteAsync(cancellationToken);

        var session = RemoteSession.Request(
            tenantContext.RequireTenantId(), deviceId, currentUser.RequireUserId(), await TechnicianNameAsync(cancellationToken), request.Reason, now);
        db.Set<RemoteSession>().Add(session);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("There is already an open remote session on this device.", "remote_session_open");
        }

        LogRequested(logger, session.Id, deviceId, session.RequestedByUserId, mode);
        return await GetAsync(session.Id, cancellationToken);
    }

    public async Task<PagedResult<RemoteSessionDto>> ListAsync(RemoteSessionListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var sessions = db.Set<RemoteSession>().AsNoTracking();
        if (query.DeviceId is { } deviceId)
        {
            sessions = sessions.Where(s => s.DeviceId == deviceId);
        }

        var total = await sessions.LongCountAsync(cancellationToken);
        var items = await Project(sessions.OrderByDescending(s => s.CreatedAtUtc).Skip(page.Skip).Take(page.SafePageSize)).ToListAsync(cancellationToken);
        return new PagedResult<RemoteSessionDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<RemoteSessionDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await ReapAsync([await FindAsync(id, cancellationToken)], clock.UtcNow, cancellationToken);
        return await Project(db.Set<RemoteSession>().AsNoTracking().Where(s => s.Id == id)).FirstAsync(cancellationToken);
    }

    public async Task<RemoteOutputDto> OutputAsync(Guid id, long after, int waitSeconds, CancellationToken cancellationToken)
    {
        await ReapAsync([await FindAsync(id, cancellationToken)], clock.UtcNow, cancellationToken);
        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var chunks = await db.Set<RemoteChunkRow>().AsNoTracking()
                .Where(c => c.SessionId == id && c.Direction == RemoteDirection.FromDevice && c.Sequence > after)
                .OrderBy(c => c.Sequence)
                .Take(200)
                .Select(c => new RemoteChunk { Sequence = c.Sequence, Text = c.Text })
                .ToListAsync(cancellationToken);
            var state = await db.Set<RemoteSession>().AsNoTracking().Where(s => s.Id == id).Select(s => new { s.Status, s.EndReason }).FirstAsync(cancellationToken);
            if (chunks.Count > 0 || state.Status is not (RemoteSessionStatus.Pending or RemoteSessionStatus.Active) || timer.Elapsed >= wait)
            {
                return new RemoteOutputDto(chunks, state.Status, state.EndReason);
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new RemoteOutputDto([], state.Status, state.EndReason);
            }
        }
    }

    public async Task SendInputAsync(Guid id, RemoteInputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await FindAsync(id, cancellationToken);
        await ReapAsync([session], clock.UtcNow, cancellationToken);
        if (session.Status != RemoteSessionStatus.Active)
        {
            throw new ConflictException("The session is not active.", "remote_session_not_active");
        }

        db.Set<RemoteChunkRow>().Add(RemoteChunkRow.Create(session.TenantId, id, RemoteDirection.ToDevice, request.Text));
        session.Touch(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RemoteSessionDto> EndAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await FindAsync(id, cancellationToken);
        if (session.IsOpen)
        {
            session.End($"Ended by {await TechnicianNameAsync(cancellationToken)}.", clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            LogEnded(logger, id, session.Status);
        }

        return await GetAsync(id, cancellationToken);
    }

    /// <summary>The mode the computer last reported in its heartbeat (disabled when unknown).</summary>
    internal static RemoteAccessMode ReportedMode(Device device)
    {
        if (device.AgentHealthJson is null)
        {
            return RemoteAccessMode.Disabled;
        }

        try
        {
            var health = JsonSerializer.Deserialize<AgentHealthReport>(device.AgentHealthJson, HealthJson);
            return health?.RemoteAccess ?? RemoteAccessMode.Disabled;
        }
        catch (JsonException)
        {
            return RemoteAccessMode.Disabled;
        }
    }

    private async Task ReapAsync(IEnumerable<RemoteSession> sessions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var session in sessions)
        {
            changed |= session.ApplyTimeouts(now);
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<RemoteSession> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<RemoteSession>().FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
        ?? throw new NotFoundException("Remote session", id);

    private async Task<string> TechnicianNameAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var found = await members.FindAsync([userId], cancellationToken);
        return found.TryGetValue(userId, out var member) ? member.DisplayName : currentUser.Email ?? "Technician";
    }

    private IQueryable<RemoteSessionDto> Project(IQueryable<RemoteSession> sessions) =>
        from s in sessions
        join d in db.Set<Device>().AsNoTracking() on s.DeviceId equals d.Id into devices
        from d in devices.DefaultIfEmpty()
        select new RemoteSessionDto(
            s.Id, s.DeviceId, d == null ? null : (d.DisplayName ?? d.Hostname), s.RequestedByName, s.Reason, s.Status, s.Consent,
            s.CreatedAtUtc, s.StartedAtUtc, s.EndedAtUtc, s.EndReason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote session {SessionId} requested on device {DeviceId} by user {UserId} (device policy {Mode})")]
    private static partial void LogRequested(ILogger logger, Guid sessionId, Guid deviceId, Guid userId, RemoteAccessMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote session {SessionId} ended by a technician ({Status})")]
    private static partial void LogEnded(ILogger logger, Guid sessionId, RemoteSessionStatus status);
}
