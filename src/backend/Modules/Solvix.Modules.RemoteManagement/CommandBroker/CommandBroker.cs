namespace Solvix.Modules.RemoteManagement.CommandBroker;

using System.Globalization;
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
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface ICommandBroker
{
    Task<CommandExecutionDto> RequestAsync(Guid deviceId, string commandType, OperationRequest request, CancellationToken cancellationToken);

    Task<CommandExecutionDto> GetAsync(Guid executionId, CancellationToken cancellationToken);

    Task<PagedResult<CommandExecutionDto>> ListAsync(CommandListQuery query, CancellationToken cancellationToken);

    Task<CommandExecutionDto> CancelAsync(Guid executionId, CancellationToken cancellationToken);

    Task<PagedResult<ApprovalDto>> ListApprovalsAsync(ApprovalStatus? status, int page, int pageSize, CancellationToken cancellationToken);

    Task<ApprovalDto> ApproveAsync(Guid approvalId, string? comment, CancellationToken cancellationToken);

    Task<ApprovalDto> RejectAsync(Guid approvalId, string? comment, CancellationToken cancellationToken);
}

/// <summary>
/// Creates, authorizes, signs and tracks commands. The browser only names an allow-listed
/// operation and a device; tenant, requester, sequence, nonce, expiry and approval state
/// are filled in here from server-side data, and the envelope is signed before it is stored.
/// </summary>
internal sealed class CommandBroker(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    RemoteAuthorizer authorizer,
    RemoteAccessPolicyProvider policies,
    RemoteAuditTrail audit,
    ICommandSigner signer,
    CommandSignal signal,
    IHttpContextAccessor httpContext,
    IClock clock) : ICommandBroker
{
    public async Task<CommandExecutionDto> RequestAsync(Guid deviceId, string commandType, OperationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CommandTypes.Software.Contains(commandType))
        {
            throw new ValidationException("commandType", "Software commands are only created by software deployments.");
        }

        var definition = CommandDefinitions.Get(commandType);
        var parameters = definition.Validate(ParametersOf(request));
        if (request.Reason is { Length: > 500 })
        {
            throw new ValidationException(nameof(request.Reason), "The reason must be at most 500 characters.");
        }

        var device = await authorizer.AuthorizeAsync(deviceId, definition.Permission, commandType, cancellationToken);
        if (definition.RequiresTargetConfirmation
            && !string.Equals(request.ConfirmDeviceName?.Trim(), device.Endpoint.Hostname, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException(nameof(request.ConfirmDeviceName), $"Type the device name ({device.Endpoint.Hostname}) to confirm this operation.");
        }

        var policy = await policies.GetAsync(cancellationToken);
        var now = clock.UtcNow;
        var execution = new CommandExecution(
            tenantContext.RequireTenantId(),
            deviceId,
            device.Endpoint.Hostname,
            currentUser.RequireUserId(),
            commandType,
            definition.Permission,
            parameters,
            string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            now,
            now.AddMinutes(policy.CommandTtlMinutes),
            definition.ExecutionTimeout,
            httpContext.HttpContext?.Connection.RemoteIpAddress?.ToString());

        await audit.AtomicAsync(
            async ct =>
            {
                db.Set<CommandExecution>().Add(execution);
                audit.Record(Entry(RemoteAuditEvents.CommandRequested, RemoteAuditOutcome.Success, execution, Describe(parameters, execution.Reason)));
                if (policy.RequiresApproval(commandType))
                {
                    var approval = new RemoteApproval(
                        execution.TenantId, execution.Id, execution.RequestedBy, commandType, deviceId, execution.DeviceName, execution.Reason,
                        now.AddMinutes(policy.ApprovalTtlMinutes));
                    db.Set<RemoteApproval>().Add(approval);
                    execution.Status = CommandExecutionStatus.PendingApproval;
                    execution.ApprovalId = approval.Id;
                    execution.ExpiresAtUtc = approval.ExpiresAtUtc;
                    audit.Record(Entry(RemoteAuditEvents.ApprovalRequested, RemoteAuditOutcome.Success, execution) with { ApprovalId = approval.Id });
                }
                else
                {
                    await QueueAsync(execution, now, ct);
                }

                return true;
            },
            cancellationToken);

        if (execution.Status == CommandExecutionStatus.Queued)
        {
            signal.Notify(deviceId);
        }

        return CommandExecutionDto.From(execution);
    }

    public async Task<CommandExecutionDto> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
        CommandExecutionDto.From(await FindAsync(executionId, tracked: false, cancellationToken));

    public async Task<PagedResult<CommandExecutionDto>> ListAsync(CommandListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);
        var executions = db.Set<CommandExecution>().AsNoTracking().Where(e => e.CommandType != CommandTypes.Cancel);
        if (query.DeviceId is { } deviceId)
        {
            executions = executions.Where(e => e.DeviceId == deviceId);
        }

        if (query.Status is { } status)
        {
            executions = executions.Where(e => e.Status == status);
        }

        var total = await executions.LongCountAsync(cancellationToken);
        var rows = await executions.OrderByDescending(e => e.RequestedAtUtc).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new PagedResult<CommandExecutionDto>(rows.Select(CommandExecutionDto.From).ToList(), page, size, total);
    }

    public async Task<CommandExecutionDto> CancelAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var snapshot = await FindAsync(executionId, tracked: false, cancellationToken);
        if (snapshot.CommandType == CommandTypes.Cancel)
        {
            throw new NotFoundException("Command", executionId);
        }

        // Cancelling needs the same right as requesting (and the device must still be in scope).
        await authorizer.AuthorizeAsync(snapshot.DeviceId, snapshot.RequiredPermission, snapshot.CommandType, cancellationToken);
        return await CancelCoreAsync(executionId, currentUser.RequireUserId(), throwIfFinished: true, cancellationToken);
    }

    /// <summary>
    /// Cancels without authorizing a signed-in user (the caller did that). Before acknowledgement the command
    /// is cancelled at once; afterwards a signed <c>command.cancel</c> is queued for the device.
    /// </summary>
    internal async Task<CommandExecutionDto> CancelCoreAsync(Guid executionId, Guid userId, bool throwIfFinished, CancellationToken cancellationToken)
    {
        var snapshot = await FindAsync(executionId, tracked: false, cancellationToken);
        var now = clock.UtcNow;
        var notify = false;
        var result = await audit.AtomicAsync(
            async ct =>
            {
                var execution = await FindAsync(executionId, tracked: true, ct);
                switch (execution.Status)
                {
                    case CommandExecutionStatus.PendingApproval:
                    case CommandExecutionStatus.Queued:
                    case CommandExecutionStatus.Delivered:
                        // A device that already holds a Delivered command must acknowledge it before running it;
                        // that acknowledgement now fails, so the command is never executed.
                        execution.Status = CommandExecutionStatus.Cancelled;
                        execution.CancelRequestedAtUtc = now;
                        execution.CancelRequestedBy = userId;
                        execution.CompletedAtUtc = now;
                        // Delegated commands (software deployments) carry their owner's approval id, not a remote approval.
                        if (execution.ApprovalId is { } approvalId
                            && await db.Set<RemoteApproval>().FirstOrDefaultAsync(a => a.Id == approvalId, ct) is { Status: ApprovalStatus.Pending } approval)
                        {
                            approval.Status = ApprovalStatus.Cancelled;
                        }

                        audit.Record(Entry(RemoteAuditEvents.CommandCancelled, RemoteAuditOutcome.Success, execution));
                        break;
                    case CommandExecutionStatus.Acknowledged when execution.CancelRequestedAtUtc is null:
                        execution.CancelRequestedAtUtc = now;
                        execution.CancelRequestedBy = userId;
                        var cancel = new CommandExecution(
                            execution.TenantId, execution.DeviceId, execution.DeviceName, userId, CommandTypes.Cancel,
                            CommandDefinitions.Cancel.Permission,
                            new Dictionary<string, string> { ["executionId"] = execution.Id.ToString("D") },
                            null, now, now.AddMinutes(10), CommandDefinitions.Cancel.ExecutionTimeout, execution.SourceIp)
                        {
                            TargetExecutionId = execution.Id,
                        };
                        db.Set<CommandExecution>().Add(cancel);
                        await QueueAsync(cancel, now, ct);
                        notify = true;
                        audit.Record(Entry(RemoteAuditEvents.CommandCancelRequested, RemoteAuditOutcome.Success, execution, $"cancel command {cancel.Id:D}"));
                        break;
                    case CommandExecutionStatus.Acknowledged:
                        break; // already asked
                    default:
                        if (throwIfFinished)
                        {
                            throw new ConflictException("The command has already finished.", "command_finished");
                        }

                        break;
                }

                return CommandExecutionDto.From(execution);
            },
            cancellationToken);

        if (notify)
        {
            signal.Notify(snapshot.DeviceId);
        }

        return result;
    }

    public async Task<PagedResult<ApprovalDto>> ListApprovalsAsync(ApprovalStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var approvals = db.Set<RemoteApproval>().AsNoTracking();
        if (status is { } s)
        {
            approvals = approvals.Where(a => a.Status == s);
        }

        var total = await approvals.LongCountAsync(cancellationToken);
        var rows = await approvals.OrderByDescending(a => a.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<ApprovalDto>(rows.Select(ApprovalDto.From).ToList(), page, pageSize, total);
    }

    public async Task<ApprovalDto> ApproveAsync(Guid approvalId, string? comment, CancellationToken cancellationToken)
    {
        var (approval, execution) = await LoadPendingApprovalAsync(approvalId, cancellationToken);

        // The approver must be allowed to perform the operation on that device themselves.
        await authorizer.AuthorizeAsync(execution.DeviceId, execution.RequiredPermission, execution.CommandType, cancellationToken);

        // …and the requester must still be allowed to have asked for it.
        var requesterDenial = await authorizer.ReauthorizeAsync(execution.RequestedBy, execution.DeviceId, execution.RequiredPermission, cancellationToken);
        var now = clock.UtcNow;
        var userId = currentUser.RequireUserId();
        var result = await audit.AtomicAsync(
            async ct =>
            {
                var (a, e) = await LoadPendingApprovalAsync(approvalId, ct, tracked: true);
                a.DecidedBy = userId;
                a.DecidedAtUtc = now;
                a.DecisionComment = Clean(comment);
                if (requesterDenial is not null)
                {
                    a.Status = ApprovalStatus.Cancelled;
                    Finish(e, CommandExecutionStatus.Rejected, now, requesterDenial, "The requester is no longer allowed to perform this operation.");
                    audit.Record(Entry(RemoteAuditEvents.CommandRejectedAtDelivery, RemoteAuditOutcome.Denied, e, requesterDenial) with { ApprovalId = a.Id });
                    return ApprovalDto.From(a);
                }

                a.Status = ApprovalStatus.Approved;
                e.ApprovedBy = userId;
                var policy = await policies.GetAsync(ct);
                e.ExpiresAtUtc = now.AddMinutes(policy.CommandTtlMinutes);
                audit.Record(Entry(RemoteAuditEvents.ApprovalApproved, RemoteAuditOutcome.Success, e, a.DecisionComment) with { ApprovalId = a.Id });
                await QueueAsync(e, now, ct);
                return ApprovalDto.From(a);
            },
            cancellationToken);

        signal.Notify(execution.DeviceId);
        return result;
    }

    public async Task<ApprovalDto> RejectAsync(Guid approvalId, string? comment, CancellationToken cancellationToken)
    {
        var (_, execution) = await LoadPendingApprovalAsync(approvalId, cancellationToken);
        await authorizer.AuthorizeAsync(execution.DeviceId, Permissions.Remote.ApprovalsManage, execution.CommandType, cancellationToken);
        var now = clock.UtcNow;
        var userId = currentUser.RequireUserId();
        return await audit.AtomicAsync(
            async ct =>
            {
                var (a, e) = await LoadPendingApprovalAsync(approvalId, ct, tracked: true);
                a.Status = ApprovalStatus.Rejected;
                a.DecidedBy = userId;
                a.DecidedAtUtc = now;
                a.DecisionComment = Clean(comment);
                Finish(e, CommandExecutionStatus.ApprovalRejected, now, "approval_rejected", a.DecisionComment);
                audit.Record(Entry(RemoteAuditEvents.ApprovalRejected, RemoteAuditOutcome.Success, e, a.DecisionComment) with { ApprovalId = a.Id });
                return ApprovalDto.From(a);
            },
            cancellationToken);
    }

    /// <summary>Allocates the next device sequence, builds and signs the envelope and marks the execution Queued.</summary>
    internal async Task QueueAsync(CommandExecution execution, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = execution.TenantId;
        var deviceId = execution.DeviceId;
        var sequence = (await db.Database.SqlQuery<long>($"""
            INSERT INTO remote_device_command_sequences (id, tenant_id, device_id, last_sequence, created_at_utc, updated_at_utc)
            VALUES (gen_random_uuid(), {tenantId}, {deviceId}, 1, now(), now())
            ON CONFLICT (tenant_id, device_id)
            DO UPDATE SET last_sequence = remote_device_command_sequences.last_sequence + 1, updated_at_utc = now()
            RETURNING last_sequence AS "Value"
            """).ToListAsync(cancellationToken)).Single();

        var envelope = new CommandEnvelope
        {
            ExecutionId = execution.Id,
            TenantId = tenantId,
            DeviceId = deviceId,
            RequestedBy = execution.RequestedBy,
            CommandType = execution.CommandType,
            Parameters = execution.Parameters,
            Sequence = sequence,
            Nonce = CommandSignature.NewNonce(),
            CreatedAtUtc = now,
            ExpiresAtUtc = execution.ExpiresAtUtc,
            Authorization = CommandAuthorization.Authorized,
            ApprovalStatus = execution.ApprovalId is null ? CommandApprovalStatus.NotRequired : CommandApprovalStatus.Approved,
            ApprovalId = execution.ApprovalId,
            ApprovedBy = execution.ApprovedBy,
            AuditId = execution.Id,
            KeyId = signer.KeyId,
        };
        var signed = signer.Sign(envelope);
        execution.Sequence = sequence;
        execution.KeyId = signed.KeyId;
        execution.SignedCommand = JsonSerializer.Serialize(signed, AgentJsonContext.Default.SignedCommand);
        execution.Status = CommandExecutionStatus.Queued;
        execution.QueuedAtUtc = now;
        audit.Record(Entry(RemoteAuditEvents.CommandQueued, RemoteAuditOutcome.Success, execution, $"sequence {sequence.ToString(CultureInfo.InvariantCulture)}, key {signed.KeyId}"));
    }

    internal static void Finish(CommandExecution execution, CommandExecutionStatus status, DateTimeOffset now, string? errorCode, string? message)
    {
        execution.Status = status;
        execution.CompletedAtUtc = now;
        execution.ErrorCode = errorCode;
        execution.ErrorMessage = message;
    }

    internal static RemoteAuditEntry Entry(string eventType, RemoteAuditOutcome outcome, CommandExecution execution, string? detail = null) =>
        new(eventType, outcome, execution.DeviceId, execution.Id, execution.ApprovalId, execution.CommandType, execution.RequiredPermission, detail);

    private async Task<(RemoteApproval Approval, CommandExecution Execution)> LoadPendingApprovalAsync(Guid approvalId, CancellationToken cancellationToken, bool tracked = false)
    {
        var approvals = tracked ? db.Set<RemoteApproval>() : db.Set<RemoteApproval>().AsNoTracking();
        var approval = await approvals.FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken)
            ?? throw new NotFoundException("Approval", approvalId);
        if (approval.Status != ApprovalStatus.Pending)
        {
            throw new ConflictException("This request has already been decided.", "approval_decided");
        }

        if (approval.ExpiresAtUtc <= clock.UtcNow)
        {
            throw new ConflictException("This request has expired.", "approval_expired");
        }

        if (approval.RequestedBy == currentUser.RequireUserId())
        {
            throw new ForbiddenException("You cannot approve or reject your own request.", "approval_self_decision");
        }

        var execution = await FindAsync(approval.ExecutionId, tracked, cancellationToken);
        return (approval, execution);
    }

    private async Task<CommandExecution> FindAsync(Guid executionId, bool tracked, CancellationToken cancellationToken)
    {
        var executions = tracked ? db.Set<CommandExecution>() : db.Set<CommandExecution>().AsNoTracking();
        return await executions.FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken)
            ?? throw new NotFoundException("Command", executionId);
    }

    private static Dictionary<string, string?> ParametersOf(OperationRequest request)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (request.DelaySeconds is { } delay)
        {
            parameters["delaySeconds"] = delay.ToString(CultureInfo.InvariantCulture);
        }

        if (request.Message is not null)
        {
            parameters["message"] = request.Message;
        }

        return parameters;
    }

    private static string? Describe(IReadOnlyDictionary<string, string> parameters, string? reason)
    {
        var parts = parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}").ToList();
        if (reason is not null)
        {
            parts.Add($"reason={reason}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string? Clean(string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 500)];
}
