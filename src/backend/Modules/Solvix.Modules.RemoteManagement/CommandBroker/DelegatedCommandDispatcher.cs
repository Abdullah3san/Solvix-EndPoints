namespace Solvix.Modules.RemoteManagement.CommandBroker;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Modules.RemoteManagement.Auditing;
using Solvix.Modules.RemoteManagement.Authorization;
using Solvix.Modules.RemoteManagement.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Signed commands on behalf of another module (software deployments). The requester and the
/// approval come from that module's records; the requester is re-authorized here (permission,
/// device state, technician scope) and again at delivery, exactly like an interactive request.
/// Only the delegated command types are accepted, so this cannot be used to send arbitrary commands.
/// </summary>
internal sealed class DelegatedCommandDispatcher(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    IEndpointDirectory endpoints,
    RemoteAuthorizer authorizer,
    RemoteAuditTrail audit,
    CommandBroker broker,
    CommandSignal signal,
    IClock clock) : IRemoteCommandDispatcher
{
    public async Task<RemoteCommandState> DispatchAsync(DelegatedCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = CommandDefinitions.FindDelegated(request.CommandType)
            ?? throw new InvalidOperationException($"'{request.CommandType}' cannot be dispatched by another module.");
        if (request.ExecutionId == Guid.Empty)
        {
            throw new ArgumentException("An execution id is required.", nameof(request));
        }

        if (await FindAsync(request.ExecutionId, cancellationToken) is { } existing)
        {
            return ToState(existing);
        }

        var denial = await authorizer.ReauthorizeAsync(request.RequestedBy, request.DeviceId, definition.Permission, cancellationToken, enforceAllowedHours: false);
        var device = await endpoints.FindForAccessAsync(request.DeviceId, cancellationToken);
        var now = clock.UtcNow;
        var expires = request.ExpiresAtUtc > now ? request.ExpiresAtUtc : now.AddMinutes(RemoteAccessPolicy.MinCommandTtlMinutes);

        // Never longer than the agent accepts (validity window ≤ 8 days).
        if (expires - now > TimeSpan.FromDays(7))
        {
            expires = now.AddDays(7);
        }

        var queued = false;
        var execution = await audit.AtomicAsync(
            async ct =>
            {
                // Serialized per tenant by the audit lock: the existence check cannot race.
                if (await FindAsync(request.ExecutionId, ct) is { } raced)
                {
                    return raced;
                }

                var created = new CommandExecution(
                    tenantContext.RequireTenantId(),
                    request.DeviceId,
                    device?.Endpoint.Hostname ?? "unknown",
                    request.RequestedBy,
                    request.CommandType,
                    definition.Permission,
                    new Dictionary<string, string>(request.Parameters, StringComparer.Ordinal),
                    request.Reason is { Length: > 500 } r ? r[..500] : request.Reason,
                    now,
                    expires,
                    request.ExecutionTimeout,
                    sourceIp: null,
                    id: request.ExecutionId)
                {
                    ApprovalId = request.ApprovalId,
                    ApprovedBy = request.ApprovedBy,
                };
                db.Set<CommandExecution>().Add(created);
                var entry = CommandBroker.Entry(RemoteAuditEvents.CommandRequested, RemoteAuditOutcome.Success, created, created.Reason) with { ActorUserId = request.RequestedBy };
                audit.Record(entry);
                if (denial is not null)
                {
                    CommandBroker.Finish(created, CommandExecutionStatus.Rejected, now, denial, "The requester is not allowed to perform this operation on this device.");
                    audit.Record(CommandBroker.Entry(RemoteAuditEvents.CommandRejectedAtDelivery, RemoteAuditOutcome.Denied, created, denial) with { ActorUserId = request.RequestedBy });
                    return created;
                }

                await broker.QueueAsync(created, now, ct);
                queued = true;
                return created;
            },
            cancellationToken);

        if (queued)
        {
            signal.Notify(request.DeviceId);
        }

        return ToState(execution);
    }

    public async Task<IReadOnlyList<RemoteCommandState>> GetStatesAsync(IReadOnlyCollection<Guid> executionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionIds);
        if (executionIds.Count == 0)
        {
            return [];
        }

        var ids = executionIds.Distinct().ToList();
        var rows = await db.Set<CommandExecution>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken);
        return rows.Select(ToState).ToList();
    }

    public async Task<RemoteCommandState?> CancelAsync(Guid executionId, Guid cancelledBy, CancellationToken cancellationToken)
    {
        var execution = await FindAsync(executionId, cancellationToken);
        if (execution is null || !CommandTypes.Software.Contains(execution.CommandType))
        {
            return null;
        }

        if (!execution.IsFinal())
        {
            await broker.CancelCoreAsync(executionId, cancelledBy, throwIfFinished: false, cancellationToken);
        }

        return ToState((await FindAsync(executionId, cancellationToken))!);
    }

    public async Task<RemoteCommandState?> FindForDeviceAsync(Guid executionId, Guid deviceId, CancellationToken cancellationToken)
    {
        var execution = await db.Set<CommandExecution>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executionId && e.DeviceId == deviceId, cancellationToken);
        return execution is null ? null : ToState(execution);
    }

    internal static RemoteCommandState ToState(CommandExecution e) => new(
        e.Id,
        e.DeviceId,
        e.DeviceName,
        e.CommandType,
        PhaseOf(e.Status),
        e.Parameters,
        e.AcknowledgedAtUtc,
        e.CompletedAtUtc,
        e.ExitCode,
        e.Output,
        e.ErrorCode,
        e.ErrorMessage);

    internal static RemoteCommandPhase PhaseOf(CommandExecutionStatus status) => status switch
    {
        CommandExecutionStatus.PendingApproval or CommandExecutionStatus.Queued or CommandExecutionStatus.Delivered => RemoteCommandPhase.Pending,
        CommandExecutionStatus.Acknowledged => RemoteCommandPhase.Running,
        CommandExecutionStatus.Succeeded => RemoteCommandPhase.Succeeded,
        CommandExecutionStatus.Failed => RemoteCommandPhase.Failed,
        CommandExecutionStatus.Cancelled => RemoteCommandPhase.Cancelled,
        CommandExecutionStatus.Expired => RemoteCommandPhase.Expired,
        CommandExecutionStatus.TimedOut => RemoteCommandPhase.TimedOut,
        _ => RemoteCommandPhase.Rejected,
    };

    private Task<CommandExecution?> FindAsync(Guid executionId, CancellationToken cancellationToken) =>
        db.Set<CommandExecution>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);
}

/// <summary>Tells interested modules that a command finished; never fails the caller.</summary>
internal sealed partial class RemoteCommandObserverPublisher(
    IEnumerable<IRemoteCommandObserver> observers,
    ISolvixDbContext db,
    ILogger<RemoteCommandObserverPublisher> logger)
{
    public async Task PublishAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var interested = observers.ToList();
        if (interested.Count == 0)
        {
            return;
        }

        try
        {
            var execution = await db.Set<CommandExecution>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);
            if (execution is null)
            {
                return;
            }

            var state = DelegatedCommandDispatcher.ToState(execution);
            foreach (var observer in interested.Where(o => execution.CommandType.StartsWith(o.CommandTypePrefix, StringComparison.Ordinal)))
            {
                await observer.OnCommandFinishedAsync(state, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogObserverFailed(logger, ex, executionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A command observer failed for execution {ExecutionId}; it is reconciled later")]
    private static partial void LogObserverFailed(ILogger logger, Exception exception, Guid executionId);
}
