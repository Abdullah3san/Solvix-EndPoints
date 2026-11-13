namespace Solvix.Modules.RemoteManagement.CommandBroker;

using Solvix.Modules.RemoteManagement.Domain;

public sealed record CommandExecutionDto(
    Guid Id,
    Guid DeviceId,
    string DeviceName,
    string CommandType,
    CommandExecutionStatus Status,
    Guid RequestedBy,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyDictionary<string, string> Parameters,
    string? Reason,
    Guid? ApprovalId,
    Guid? ApprovedBy,
    long? Sequence,
    int DeliveryAttempts,
    DateTimeOffset? LastDeliveredAtUtc,
    DateTimeOffset? AcknowledgedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    double? DurationSeconds,
    int? ExitCode,
    string? Output,
    bool OutputTruncated,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? CancelRequestedAtUtc)
{
    internal static CommandExecutionDto From(CommandExecution e) => new(
        e.Id,
        e.DeviceId,
        e.DeviceName,
        e.CommandType,
        e.Status,
        e.RequestedBy,
        e.RequestedAtUtc,
        e.ExpiresAtUtc,
        e.Parameters,
        e.Reason,
        e.ApprovalId,
        e.ApprovedBy,
        e.Sequence,
        e.DeliveryAttempts,
        e.LastDeliveredAtUtc,
        e.AcknowledgedAtUtc,
        e.CompletedAtUtc,
        e.StartedAtUtc is { } s && e.CompletedAtUtc is { } c ? (c - s).TotalSeconds : null,
        e.ExitCode,
        e.Output,
        e.OutputTruncated,
        e.ErrorCode,
        e.ErrorMessage,
        e.CancelRequestedAtUtc);
}

/// <summary>Body of every operation request. The command type comes from the route, never from here.</summary>
public sealed record OperationRequest
{
    /// <summary>For disruptive operations: the device's hostname, typed by the requester to confirm the target.</summary>
    public string? ConfirmDeviceName { get; init; }

    public string? Reason { get; init; }

    public int? DelaySeconds { get; init; }

    /// <summary>Shown to the signed-in user before a restart, shutdown or sign-off.</summary>
    public string? Message { get; init; }
}

public sealed record CommandListQuery
{
    public Guid? DeviceId { get; init; }

    public CommandExecutionStatus? Status { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;
}

public sealed record ApprovalDto(
    Guid Id,
    Guid ExecutionId,
    Guid DeviceId,
    string DeviceName,
    string CommandType,
    Guid RequestedBy,
    string? Reason,
    ApprovalStatus Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionComment)
{
    internal static ApprovalDto From(RemoteApproval a) => new(
        a.Id, a.ExecutionId, a.DeviceId, a.DeviceName, a.CommandType, a.RequestedBy, a.Reason, a.Status,
        a.CreatedAtUtc, a.ExpiresAtUtc, a.DecidedBy, a.DecidedAtUtc, a.DecisionComment);
}

public sealed record ApprovalDecisionRequest(string? Comment);
