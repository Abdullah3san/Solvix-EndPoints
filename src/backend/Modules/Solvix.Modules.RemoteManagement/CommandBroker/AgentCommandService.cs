namespace Solvix.Modules.RemoteManagement.CommandBroker;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Modules.RemoteManagement.Auditing;
using Solvix.Modules.RemoteManagement.Authorization;
using Solvix.Modules.RemoteManagement.Configuration;
using Solvix.Modules.RemoteManagement.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;

/// <summary>The device behind the current agent request, from its CA-signed certificate.</summary>
public sealed class CurrentAgentDevice(IHttpContextAccessor httpContext)
{
    public Guid DeviceId =>
        Guid.TryParse(httpContext.HttpContext?.User.FindFirst(SolvixClaimTypes.DeviceId)?.Value, out var id)
            ? id
            : throw new UnauthorizedException("A device certificate is required.");
}

public interface IAgentCommandService
{
    Task<AgentCommandBatch> FetchAsync(int waitSeconds, CancellationToken cancellationToken);

    Task AcknowledgeAsync(Guid executionId, CommandAcknowledgement acknowledgement, CancellationToken cancellationToken);

    Task ReportResultAsync(Guid executionId, CommandResultReport report, CancellationToken cancellationToken);

    CommandKeySet GetKeySet();
}

/// <summary>
/// The device side of the broker. A device only ever sees, acknowledges and reports on its own
/// commands (device id from the certificate; tenant from the certificate via RLS). Delivery is
/// at-least-once (redelivered until acknowledged or expired); execution is at-most-once because
/// the agent runs a command only after the server accepted its acknowledgement, and a result is
/// recorded only for an acknowledged command, once.
/// </summary>
internal sealed class AgentCommandService(
    ISolvixDbContext db,
    CurrentAgentDevice device,
    RemoteAuthorizer authorizer,
    RemoteAuditTrail audit,
    ICommandSigner signer,
    CommandSignal signal,
    RemoteCommandObserverPublisher observers,
    IOptions<RemoteManagementOptions> options,
    IClock clock) : IAgentCommandService, IRemoteCommandQueue
{
    public CommandKeySet GetKeySet() => signer.KeySet;

    public async Task<AgentCommandBatch> FetchAsync(int waitSeconds, CancellationToken cancellationToken)
    {
        var deviceId = device.DeviceId;
        var batch = await DeliverAsync(deviceId, cancellationToken);
        if (batch.Count == 0 && waitSeconds > 0)
        {
            var wait = TimeSpan.FromSeconds(Math.Min(waitSeconds, Math.Clamp(options.Value.LongPollSeconds, 0, 30)));
            await signal.WaitAsync(deviceId, wait, cancellationToken);
            batch = await DeliverAsync(deviceId, cancellationToken);
        }

        return new AgentCommandBatch { Commands = batch };
    }

    public async Task<int> CountPendingAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var redeliverBefore = now.AddSeconds(-options.Value.RedeliveryAfterSeconds);
        return await Deliverable(deviceId, now, redeliverBefore).CountAsync(cancellationToken);
    }

    public async Task AcknowledgeAsync(Guid executionId, CommandAcknowledgement acknowledgement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        var deviceId = device.DeviceId;
        var now = clock.UtcNow;
        await audit.AtomicAsync(
            async ct =>
            {
                var execution = await OwnAsync(executionId, deviceId, ct);
                if (acknowledgement.Accepted)
                {
                    if (execution.Status == CommandExecutionStatus.Acknowledged)
                    {
                        return true; // retried acknowledgement
                    }

                    var updated = await db.Set<CommandExecution>()
                        .Where(e => e.Id == executionId && e.Status == CommandExecutionStatus.Delivered && e.ExpiresAtUtc > now)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(e => e.Status, CommandExecutionStatus.Acknowledged)
                                .SetProperty(e => e.AcknowledgedAtUtc, now)
                                .SetProperty(e => e.StartedAtUtc, now)
                                .SetProperty(e => e.ExecutionDeadlineUtc, now.AddSeconds(execution.ExecutionTimeoutSeconds)),
                            ct);
                    if (updated == 0)
                    {
                        // Cancelled, expired or never delivered: the agent must not run it.
                        throw new ConflictException("This command can no longer be executed.", "command_not_executable");
                    }

                    audit.Record(CommandBroker.Entry(RemoteAuditEvents.CommandAcknowledged, RemoteAuditOutcome.Success, execution));
                    return true;
                }

                var code = Truncate(acknowledgement.RejectionCode, 64) ?? "rejected";
                var rejected = await db.Set<CommandExecution>()
                    .Where(e => e.Id == executionId && (e.Status == CommandExecutionStatus.Delivered || e.Status == CommandExecutionStatus.Queued))
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(e => e.Status, CommandExecutionStatus.Rejected)
                            .SetProperty(e => e.CompletedAtUtc, now)
                            .SetProperty(e => e.ErrorCode, code)
                            .SetProperty(e => e.ErrorMessage, Truncate(acknowledgement.Detail, 2000)),
                        ct);
                if (rejected > 0)
                {
                    audit.Record(CommandBroker.Entry(RemoteAuditEvents.CommandRejectedByDevice, RemoteAuditOutcome.Denied, execution, code));
                }

                return true;
            },
            cancellationToken);

        if (!acknowledgement.Accepted)
        {
            await observers.PublishAsync(executionId, cancellationToken);
        }
    }

    public async Task ReportResultAsync(Guid executionId, CommandResultReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var deviceId = device.DeviceId;
        var now = clock.UtcNow;
        var status = report.Outcome switch
        {
            CommandOutcome.Succeeded => CommandExecutionStatus.Succeeded,
            CommandOutcome.Failed => CommandExecutionStatus.Failed,
            CommandOutcome.Cancelled => CommandExecutionStatus.Cancelled,
            CommandOutcome.TimedOut => CommandExecutionStatus.TimedOut,
            _ => throw new ValidationException(nameof(report.Outcome), "Unknown outcome."),
        };
        var output = report.Output;
        var truncated = report.OutputTruncated;
        if (output is { Length: > CommandResultReport.MaxOutputLength })
        {
            output = output[..CommandResultReport.MaxOutputLength];
            truncated = true;
        }

        var recorded = await audit.AtomicAsync(
            async ct =>
            {
                var execution = await OwnAsync(executionId, deviceId, ct);
                var updated = await db.Set<CommandExecution>()
                    .Where(e => e.Id == executionId && e.Status == CommandExecutionStatus.Acknowledged)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(e => e.Status, status)
                            .SetProperty(e => e.CompletedAtUtc, now)
                            .SetProperty(e => e.ExitCode, report.ExitCode)
                            .SetProperty(e => e.Output, output)
                            .SetProperty(e => e.OutputTruncated, truncated)
                            .SetProperty(e => e.ErrorCode, Truncate(report.ErrorCode, 64))
                            .SetProperty(e => e.ErrorMessage, Truncate(report.ErrorMessage, 2000)),
                        ct);
                if (updated == 0)
                {
                    audit.Record(CommandBroker.Entry(RemoteAuditEvents.CommandDuplicateResult, RemoteAuditOutcome.Denied, execution, execution.Status.ToString()));
                    return false;
                }

                audit.Record(CommandBroker.Entry(RemoteAuditEvents.CommandCompleted, status == CommandExecutionStatus.Succeeded ? RemoteAuditOutcome.Success : RemoteAuditOutcome.Failure, execution, $"{status}{(report.ExitCode is { } code ? $", exit {code}" : string.Empty)}"));
                return true;
            },
            cancellationToken);

        // Outside the unit of work, so the duplicate attempt itself stays in the audit trail.
        if (!recorded)
        {
            throw new ConflictException("A result was already recorded for this command.", "command_result_recorded");
        }

        await observers.PublishAsync(executionId, cancellationToken);
    }

    private async Task<IReadOnlyList<SignedCommand>> DeliverAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var redeliverBefore = now.AddSeconds(-options.Value.RedeliveryAfterSeconds);
        var candidates = await Deliverable(deviceId, now, redeliverBefore)
            .OrderBy(e => e.Sequence)
            .Take(Math.Clamp(options.Value.MaxBatchSize, 1, 50))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return [];
        }

        // Re-authorize the requester now: a role change, deactivation or scope change since the
        // request stops the command here, before the device ever sees it.
        var denials = new Dictionary<Guid, string>();
        foreach (var candidate in candidates.Where(c => c.CommandType != CommandTypes.Cancel))
        {
            if (await authorizer.ReauthorizeAsync(candidate.RequestedBy, deviceId, candidate.RequiredPermission, cancellationToken) is { } denial)
            {
                denials[candidate.Id] = denial;
            }
        }

        return await audit.AtomicAsync(
            async ct =>
            {
                var delivered = new List<SignedCommand>();
                foreach (var candidate in candidates)
                {
                    if (denials.TryGetValue(candidate.Id, out var denial))
                    {
                        var rejected = await db.Set<CommandExecution>()
                            .Where(e => e.Id == candidate.Id && (e.Status == CommandExecutionStatus.Queued || e.Status == CommandExecutionStatus.Delivered))
                            .ExecuteUpdateAsync(
                                s => s.SetProperty(e => e.Status, CommandExecutionStatus.Rejected)
                                    .SetProperty(e => e.CompletedAtUtc, now)
                                    .SetProperty(e => e.ErrorCode, denial)
                                    .SetProperty(e => e.ErrorMessage, "The requester is no longer allowed to perform this operation on this device."),
                                ct);
                        if (rejected > 0)
                        {
                            audit.Record(CommandBroker.Entry(RemoteAuditEvents.CommandRejectedAtDelivery, RemoteAuditOutcome.Denied, candidate, denial));
                        }

                        continue;
                    }

                    // Optimistic claim: only one concurrent poll can move this attempt forward.
                    var attempts = candidate.DeliveryAttempts;
                    var claimed = await db.Set<CommandExecution>()
                        .Where(e => e.Id == candidate.Id && e.DeliveryAttempts == attempts
                            && (e.Status == CommandExecutionStatus.Queued || e.Status == CommandExecutionStatus.Delivered))
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(e => e.Status, CommandExecutionStatus.Delivered)
                                .SetProperty(e => e.LastDeliveredAtUtc, now)
                                .SetProperty(e => e.DeliveryAttempts, attempts + 1),
                            ct);
                    if (claimed == 0)
                    {
                        continue;
                    }

                    if (attempts == 0)
                    {
                        audit.Record(CommandBroker.Entry(RemoteAuditEvents.CommandDelivered, RemoteAuditOutcome.Success, candidate));
                    }

                    delivered.Add(JsonSerializer.Deserialize(candidate.SignedCommand!, AgentJsonContext.Default.SignedCommand)!);
                }

                return (IReadOnlyList<SignedCommand>)delivered;
            },
            cancellationToken);
    }

    private IQueryable<CommandExecution> Deliverable(Guid deviceId, DateTimeOffset now, DateTimeOffset redeliverBefore) =>
        db.Set<CommandExecution>().Where(e =>
            e.DeviceId == deviceId
            && e.ExpiresAtUtc > now
            && e.SignedCommand != null
            && (e.Status == CommandExecutionStatus.Queued
                || (e.Status == CommandExecutionStatus.Delivered && e.LastDeliveredAtUtc < redeliverBefore)));

    private async Task<CommandExecution> OwnAsync(Guid executionId, Guid deviceId, CancellationToken cancellationToken) =>
        await db.Set<CommandExecution>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == executionId && e.DeviceId == deviceId, cancellationToken)
        ?? throw new NotFoundException("Command", executionId);

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
