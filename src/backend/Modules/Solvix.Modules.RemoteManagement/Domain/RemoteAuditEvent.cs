namespace Solvix.Modules.RemoteManagement.Domain;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Solvix.SharedKernel.Entities;

public enum RemoteAuditOutcome
{
    Success = 1,
    Denied = 2,
    Failure = 3,
}

/// <summary>Event types of the remote administration audit trail.</summary>
public static class RemoteAuditEvents
{
    public const string AccessDenied = "remote.access.denied";
    public const string CommandRequested = "command.requested";
    public const string CommandQueued = "command.queued";
    public const string CommandDelivered = "command.delivered";
    public const string CommandAcknowledged = "command.acknowledged";
    public const string CommandRejectedByDevice = "command.rejected_by_device";
    public const string CommandRejectedAtDelivery = "command.rejected_at_delivery";
    public const string CommandCompleted = "command.completed";
    public const string CommandCancelRequested = "command.cancel_requested";
    public const string CommandCancelled = "command.cancelled";
    public const string CommandExpired = "command.expired";
    public const string CommandTimedOut = "command.timed_out";
    public const string CommandDuplicateResult = "command.duplicate_result";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalApproved = "approval.approved";
    public const string ApprovalRejected = "approval.rejected";
    public const string ApprovalExpired = "approval.expired";
    public const string PolicyChanged = "policy.changed";
    public const string ScopeChanged = "scope.changed";
}

/// <summary>
/// One entry of the append-only remote administration audit trail. The table rejects
/// UPDATE, DELETE and TRUNCATE (database triggers) and every row carries the SHA-256 of
/// its content chained to the previous row of the same tenant, so a removed or edited
/// row is detected by <c>RemoteAuditTrail.VerifyAsync</c> even if the triggers were bypassed.
/// </summary>
public sealed class RemoteAuditEvent : TenantEntity
{
    private RemoteAuditEvent()
    {
    }

    internal RemoteAuditEvent(
        Guid tenantId,
        long sequence,
        DateTimeOffset occurredAtUtc,
        string eventType,
        RemoteAuditOutcome outcome,
        Guid? actorUserId,
        Guid? deviceId,
        Guid? executionId,
        Guid? approvalId,
        string? commandType,
        string? permission,
        string? sourceIp,
        string? detail,
        string previousHash)
        : base(tenantId)
    {
        Sequence = sequence;

        // PostgreSQL stores microseconds: hash what will be read back, not the in-memory ticks.
        OccurredAtUtc = new DateTimeOffset(occurredAtUtc.UtcTicks - (occurredAtUtc.UtcTicks % 10), TimeSpan.Zero);
        EventType = eventType;
        Outcome = outcome;
        ActorUserId = actorUserId;
        DeviceId = deviceId;
        ExecutionId = executionId;
        ApprovalId = approvalId;
        CommandType = commandType;
        Permission = permission;
        SourceIp = sourceIp;
        Detail = detail;
        PreviousHash = previousHash;
        Hash = ComputeHash();
    }

    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Position in the tenant's chain (1, 2, …).</summary>
    public long Sequence { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public RemoteAuditOutcome Outcome { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public Guid? DeviceId { get; private set; }

    public Guid? ExecutionId { get; private set; }

    public Guid? ApprovalId { get; private set; }

    public string? CommandType { get; private set; }

    public string? Permission { get; private set; }

    public string? SourceIp { get; private set; }

    public string? Detail { get; private set; }

    public string PreviousHash { get; private set; } = GenesisHash;

    public string Hash { get; private set; } = string.Empty;

    /// <summary>SHA-256 (hex) over the previous hash and every recorded field.</summary>
    public string ComputeHash()
    {
        var text = string.Join(
            '\u001f',
            PreviousHash,
            TenantId.ToString("D"),
            Id.ToString("D"),
            Sequence.ToString(CultureInfo.InvariantCulture),
            OccurredAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            EventType,
            ((int)Outcome).ToString(CultureInfo.InvariantCulture),
            ActorUserId?.ToString("D") ?? string.Empty,
            DeviceId?.ToString("D") ?? string.Empty,
            ExecutionId?.ToString("D") ?? string.Empty,
            ApprovalId?.ToString("D") ?? string.Empty,
            CommandType ?? string.Empty,
            Permission ?? string.Empty,
            SourceIp ?? string.Empty,
            Detail ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
