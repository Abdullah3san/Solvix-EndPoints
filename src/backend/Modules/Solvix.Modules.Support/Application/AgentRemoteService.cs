namespace Solvix.Modules.Support.Application;

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Security;
using Solvix.Modules.Support.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>What the agent does for a remote session: learn of a request, answer it, relay the terminal.</summary>
public interface IAgentRemoteService
{
    Task<PendingRemoteResponse> PendingAsync(int waitSeconds, CancellationToken cancellationToken);

    Task AcceptAsync(Guid id, AcceptRemoteRequest request, CancellationToken cancellationToken);

    Task DenyAsync(Guid id, DenyRemoteRequest request, CancellationToken cancellationToken);

    Task EndAsync(Guid id, EndRemoteRequest request, CancellationToken cancellationToken);

    /// <summary>Stores shell output. Throws a conflict when the session is no longer active so the agent stops the shell.</summary>
    Task PostOutputAsync(Guid id, RemoteOutputRequest request, CancellationToken cancellationToken);

    Task<RemoteInputResponse> InputAsync(Guid id, long after, int waitSeconds, CancellationToken cancellationToken);
}

internal sealed partial class AgentRemoteService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentDevice currentDevice,
    IClock clock,
    ILogger<AgentRemoteService> logger) : IAgentRemoteService
{
    public const int MaxWaitSeconds = 25;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    public async Task<PendingRemoteResponse> PendingAsync(int waitSeconds, CancellationToken cancellationToken)
    {
        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var pending = await db.Set<RemoteSession>().AsNoTracking()
                .Where(s => s.DeviceId == currentDevice.DeviceId && s.Status == RemoteSessionStatus.Pending)
                .OrderBy(s => s.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (pending is not null && !(clock.UtcNow - pending.CreatedAtUtc > RemoteSession.RequestTimeToLive))
            {
                return new PendingRemoteResponse
                {
                    Request = new AgentRemoteRequest
                    {
                        SessionId = pending.Id,
                        RequestedBy = pending.RequestedByName,
                        Reason = pending.Reason,
                        RequestedAtUtc = pending.CreatedAtUtc,
                    },
                };
            }

            if (timer.Elapsed >= wait)
            {
                return new PendingRemoteResponse();
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new PendingRemoteResponse();
            }
        }
    }

    public async Task AcceptAsync(Guid id, AcceptRemoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await FindAsync(id, cancellationToken);
        if (session.ApplyTimeouts(clock.UtcNow))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        session.Accept(request.Consent, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        LogAccepted(logger, id, session.DeviceId, request.Consent);
    }

    public async Task DenyAsync(Guid id, DenyRemoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await FindAsync(id, cancellationToken);
        session.Deny(request.Reason, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        LogDenied(logger, id, session.DeviceId);
    }

    public async Task EndAsync(Guid id, EndRemoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await FindAsync(id, cancellationToken);
        session.End(request.Reason ?? "Ended by the computer.", clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PostOutputAsync(Guid id, RemoteOutputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await FindAsync(id, cancellationToken);
        if (session.ApplyTimeouts(clock.UtcNow))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        if (session.Status != RemoteSessionStatus.Active)
        {
            throw new ConflictException("The session is not active.", "remote_session_not_active");
        }

        db.Set<RemoteChunkRow>().Add(RemoteChunkRow.Create(tenantContext.RequireTenantId(), id, RemoteDirection.FromDevice, request.Text));
        session.Touch(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RemoteInputResponse> InputAsync(Guid id, long after, int waitSeconds, CancellationToken cancellationToken)
    {
        var session = await FindAsync(id, cancellationToken);
        if (session.ApplyTimeouts(clock.UtcNow))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var chunks = await db.Set<RemoteChunkRow>().AsNoTracking()
                .Where(c => c.SessionId == id && c.Direction == RemoteDirection.ToDevice && c.Sequence > after)
                .OrderBy(c => c.Sequence)
                .Take(100)
                .Select(c => new RemoteChunk { Sequence = c.Sequence, Text = c.Text })
                .ToListAsync(cancellationToken);
            var status = await db.Set<RemoteSession>().AsNoTracking().Where(s => s.Id == id).Select(s => s.Status).FirstAsync(cancellationToken);
            var ended = status != RemoteSessionStatus.Active;
            if (chunks.Count > 0 || ended || timer.Elapsed >= wait)
            {
                return new RemoteInputResponse { Chunks = chunks, Ended = ended };
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new RemoteInputResponse();
            }
        }
    }

    /// <summary>A computer only ever sees its own sessions; anything else is simply not found.</summary>
    private async Task<RemoteSession> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<RemoteSession>().FirstOrDefaultAsync(s => s.Id == id && s.DeviceId == currentDevice.DeviceId, cancellationToken)
        ?? throw new NotFoundException("Remote session", id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote session {SessionId} on device {DeviceId} accepted ({Consent})")]
    private static partial void LogAccepted(ILogger logger, Guid sessionId, Guid deviceId, RemoteConsent consent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remote session {SessionId} on device {DeviceId} denied by the computer")]
    private static partial void LogDenied(ILogger logger, Guid sessionId, Guid deviceId);
}
