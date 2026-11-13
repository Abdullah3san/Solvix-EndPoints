namespace Solvix.Modules.Software.Deployments;

using Solvix.Agent.Contracts.Software;
using Solvix.Modules.Software.Domain;

/// <summary>
/// A deployment request. Targets are a deployment group and/or explicit devices, device groups and
/// branches; the package's current configuration is frozen into the deployment.
/// </summary>
public sealed record CreateDeploymentRequest
{
    public required string Name { get; init; }

    public string? Reason { get; init; }

    public required Guid PackageId { get; init; }

    public DeploymentAction Action { get; init; } = DeploymentAction.Install;

    public Guid? DeploymentGroupId { get; init; }

    public IReadOnlyList<Guid>? DeviceIds { get; init; }

    public IReadOnlyList<Guid>? DeviceGroupIds { get; init; }

    public IReadOnlyList<Guid>? BranchIds { get; init; }

    /// <summary>Null: as soon as it is approved.</summary>
    public DateTimeOffset? ScheduledStartUtc { get; init; }

    /// <summary>Null: seven days after the start. Devices not reached by then are marked expired.</summary>
    public DateTimeOffset? DeadlineUtc { get; init; }

    public int MaxAttempts { get; init; } = 3;

    public int RetryDelayMinutes { get; init; } = 15;

    /// <summary>Skip devices that already have an unfinished deployment of this package instead of refusing the request.</summary>
    public bool SkipConflicts { get; init; }
}

public sealed record PlannedTargetDto(Guid DeviceId, string DeviceName, string? OsName, string Connectivity, bool WillRun, string? SkipReason, Guid? ConflictingDeploymentId);

/// <summary>"Review deployment": everything that will happen, before anything is stored.</summary>
public sealed record DeploymentPreviewDto(
    Guid PackageId,
    string PackageName,
    string? PackageVersion,
    string PackageSha256,
    InstallerType InstallerType,
    DeploymentAction Action,
    string CommandType,
    IReadOnlyList<string> Arguments,
    DetectionRule? Detection,
    bool RequireSignature,
    int TimeoutMinutes,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset DeadlineUtc,
    int MaxAttempts,
    IReadOnlyList<PlannedTargetDto> Targets,
    int DeployableCount,
    int ConflictCount,
    bool IsSensitive,
    IReadOnlyList<string> SensitivityReasons,
    int RequiredApprovals);

public sealed record TargetCountsDto(int Total, int Pending, int InProgress, int Succeeded, int Failed, int Cancelled, int Skipped, int TimedOut, int Expired);

public sealed record DeploymentDto(
    Guid Id,
    string Name,
    string? Reason,
    DeploymentAction Action,
    Guid PackageId,
    string PackageName,
    string? PackageVersion,
    DeploymentStatus Status,
    Guid CreatedBy,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset DeadlineUtc,
    bool IsSensitive,
    IReadOnlyList<string> SensitivityReasons,
    int RequiredApprovals,
    int ApprovalsReceived,
    DateTimeOffset ApprovalExpiresAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    Guid? CancelledBy,
    int MaxAttempts,
    TargetCountsDto Counts);

public sealed record DeploymentTargetDto(
    Guid Id,
    Guid DeviceId,
    string DeviceName,
    TargetStatus Status,
    SoftwareOutcome? Outcome,
    int Attempts,
    Guid? CurrentExecutionId,
    DateTimeOffset? NextAttemptAtUtc,
    int? ExitCode,
    string? ErrorCode,
    string? ErrorMessage,
    bool RebootRequired,
    string? DetectedVersion,
    string? LogExcerpt,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc)
{
    internal static DeploymentTargetDto From(DeploymentTarget t) => new(
        t.Id, t.DeviceId, t.DeviceName, t.Status, t.Outcome, t.Attempts, t.CurrentExecutionId, t.NextAttemptAtUtc, t.ExitCode, t.ErrorCode,
        t.ErrorMessage, t.RebootRequired, t.DetectedVersion, t.LogExcerpt, t.StartedAtUtc, t.CompletedAtUtc);
}

public sealed record DeploymentApprovalDto(Guid Id, Guid ApproverId, ApprovalDecision Decision, string? Comment, DateTimeOffset DecidedAtUtc);

public sealed record DeploymentDetailDto(
    DeploymentDto Deployment,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<DeploymentApprovalDto> Approvals,
    IReadOnlyList<DeploymentTargetDto> Targets);

public sealed record DeploymentListQuery
{
    public DeploymentStatus? Status { get; init; }

    public Guid? PackageId { get; init; }

    public Guid? DeviceId { get; init; }

    /// <summary>Deployments waiting for an approval the caller can give (not their own, not yet decided by them).</summary>
    public bool AwaitingMyApproval { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;
}

public sealed record DeploymentDecisionRequest(string? Comment);

public sealed record DetectionRequestResult(Guid ExecutionId, Guid DeviceId, Guid PackageId);

public sealed record DetectionResultDto(
    Guid ExecutionId,
    Guid DeviceId,
    string Phase,
    bool? Installed,
    bool? Satisfied,
    string? DetectedVersion,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>Per-device line of a deployment report.</summary>
public sealed record DeploymentReportRow(
    Guid DeviceId,
    string DeviceName,
    TargetStatus Status,
    SoftwareOutcome? Outcome,
    int Attempts,
    int? ExitCode,
    string? ErrorCode,
    string? ErrorMessage,
    string? DetectedVersion,
    bool RebootRequired,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    double? DurationSeconds);

public sealed record FailureSummary(string ErrorCode, int Devices, string? Example);

/// <summary>"Generate deployment report": stored once when a deployment finishes; computed live before that.</summary>
public sealed record DeploymentReport(
    Guid DeploymentId,
    string Name,
    DeploymentAction Action,
    DeploymentStatus Status,
    string PackageName,
    string? PackageVersion,
    string PackageSha256,
    Guid RequestedBy,
    DateTimeOffset RequestedAtUtc,
    IReadOnlyList<DeploymentApprovalDto> Approvals,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    TargetCountsDto Counts,
    double SuccessRate,
    int RebootRequired,
    double? AverageDurationSeconds,
    IReadOnlyList<FailureSummary> Failures,
    IReadOnlyList<DeploymentReportRow> Devices,
    DateTimeOffset GeneratedAtUtc,
    bool Final);
