namespace Solvix.Modules.RemoteManagement.Domain;

using Solvix.SharedKernel.Entities;

public enum CommandExecutionStatus
{
    /// <summary>Waiting for a second person's approval; nothing has been signed yet.</summary>
    PendingApproval = 1,

    /// <summary>Signed and waiting for the device to fetch it.</summary>
    Queued = 2,

    /// <summary>Handed to the device; redelivered if not acknowledged in time.</summary>
    Delivered = 3,

    /// <summary>The device verified the signature and accepted the command.</summary>
    Acknowledged = 4,

    Succeeded = 10,
    Failed = 11,
    Cancelled = 12,
    Expired = 13,

    /// <summary>Refused by the server at delivery (authorization lost) or by the device (verification).</summary>
    Rejected = 14,

    /// <summary>Accepted by the device but no result arrived before the execution deadline.</summary>
    TimedOut = 15,

    /// <summary>The approver rejected the request.</summary>
    ApprovalRejected = 16,
}

/// <summary>
/// One command for one device: the request, its authorization, the signed envelope and the
/// outcome. Status changes are made with conditional updates (expected status in the WHERE
/// clause) by the broker, so a result is recorded exactly once.
/// </summary>
public sealed class CommandExecution : TenantEntity
{
    private CommandExecution()
    {
    }

    internal CommandExecution(
        Guid tenantId,
        Guid deviceId,
        string deviceName,
        Guid requestedBy,
        string commandType,
        string requiredPermission,
        Dictionary<string, string> parameters,
        string? reason,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        TimeSpan executionTimeout,
        string? sourceIp,
        Guid? id = null)
        : base(tenantId)
    {
        if (id is { } chosen)
        {
            Id = chosen;
        }

        DeviceId = deviceId;
        DeviceName = deviceName;
        RequestedBy = requestedBy;
        CommandType = commandType;
        RequiredPermission = requiredPermission;
        Parameters = parameters;
        Reason = reason;
        RequestedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        ExecutionTimeoutSeconds = (int)executionTimeout.TotalSeconds;
        SourceIp = sourceIp;
        Status = CommandExecutionStatus.Queued;
    }

    public Guid DeviceId { get; private set; }

    /// <summary>Hostname at request time, kept for history after the device is deleted.</summary>
    public string DeviceName { get; private set; } = string.Empty;

    public Guid RequestedBy { get; private set; }

    public string CommandType { get; private set; } = string.Empty;

    public string RequiredPermission { get; private set; } = string.Empty;

    public Dictionary<string, string> Parameters { get; private set; } = [];

    public string? Reason { get; private set; }

    public CommandExecutionStatus Status { get; internal set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; internal set; }

    public int ExecutionTimeoutSeconds { get; private set; }

    public string? SourceIp { get; private set; }

    public Guid? ApprovalId { get; internal set; }

    public Guid? ApprovedBy { get; internal set; }

    /// <summary>Per-device sequence assigned when the envelope was signed.</summary>
    public long? Sequence { get; internal set; }

    public string? KeyId { get; internal set; }

    /// <summary>The signed command exactly as delivered (JSON of <c>SignedCommand</c>); redeliveries reuse it byte for byte.</summary>
    public string? SignedCommand { get; internal set; }

    public DateTimeOffset? QueuedAtUtc { get; internal set; }

    public DateTimeOffset? LastDeliveredAtUtc { get; internal set; }

    public int DeliveryAttempts { get; internal set; }

    public DateTimeOffset? AcknowledgedAtUtc { get; internal set; }

    public DateTimeOffset? StartedAtUtc { get; internal set; }

    /// <summary>Acknowledgement time + execution timeout: no result by then means the device went silent.</summary>
    public DateTimeOffset? ExecutionDeadlineUtc { get; internal set; }

    public DateTimeOffset? CompletedAtUtc { get; internal set; }

    public int? ExitCode { get; internal set; }

    public string? Output { get; internal set; }

    public bool OutputTruncated { get; internal set; }

    public string? ErrorCode { get; internal set; }

    public string? ErrorMessage { get; internal set; }

    public DateTimeOffset? CancelRequestedAtUtc { get; internal set; }

    public Guid? CancelRequestedBy { get; internal set; }

    /// <summary>For <c>command.cancel</c>: the execution it cancels.</summary>
    public Guid? TargetExecutionId { get; internal set; }

    public static bool IsFinal(CommandExecutionStatus status) => status >= CommandExecutionStatus.Succeeded;

    public bool IsFinal() => IsFinal(Status);
}

public enum ApprovalStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Expired = 4,
    Cancelled = 5,
}

/// <summary>A request for a second person to allow a sensitive operation (four-eyes principle).</summary>
public sealed class RemoteApproval : TenantEntity
{
    private RemoteApproval()
    {
    }

    internal RemoteApproval(Guid tenantId, Guid executionId, Guid requestedBy, string commandType, Guid deviceId, string deviceName, string? reason, DateTimeOffset expiresAtUtc)
        : base(tenantId)
    {
        ExecutionId = executionId;
        RequestedBy = requestedBy;
        CommandType = commandType;
        DeviceId = deviceId;
        DeviceName = deviceName;
        Reason = reason;
        ExpiresAtUtc = expiresAtUtc;
        Status = ApprovalStatus.Pending;
    }

    public Guid ExecutionId { get; private set; }

    public Guid RequestedBy { get; private set; }

    public string CommandType { get; private set; } = string.Empty;

    public Guid DeviceId { get; private set; }

    public string DeviceName { get; private set; } = string.Empty;

    public string? Reason { get; private set; }

    public ApprovalStatus Status { get; internal set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public Guid? DecidedBy { get; internal set; }

    public DateTimeOffset? DecidedAtUtc { get; internal set; }

    public string? DecisionComment { get; internal set; }
}

/// <summary>Last sequence number allocated per device (strictly increasing).</summary>
public sealed class DeviceCommandSequence : TenantEntity
{
    private DeviceCommandSequence()
    {
    }

    public Guid DeviceId { get; private set; }

    public long LastSequence { get; private set; }
}
