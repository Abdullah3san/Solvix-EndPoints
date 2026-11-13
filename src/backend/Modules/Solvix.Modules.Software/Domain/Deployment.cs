namespace Solvix.Modules.Software.Domain;

using Solvix.Agent.Contracts.Software;
using Solvix.SharedKernel.Entities;

public enum DeploymentAction
{
    Install = 1,
    Uninstall = 2,
}

public enum DeploymentStatus
{
    /// <summary>Submitted; waiting for the required number of approvals. Nothing has been sent.</summary>
    PendingApproval = 1,

    /// <summary>Approved; waiting for its scheduled start.</summary>
    Scheduled = 2,

    /// <summary>Commands are being sent and results collected.</summary>
    Running = 3,

    Completed = 10,
    CompletedWithErrors = 11,

    /// <summary>Finished and no device succeeded.</summary>
    Failed = 12,

    Cancelled = 13,

    /// <summary>An approver rejected the deployment.</summary>
    Rejected = 14,

    /// <summary>The approval or the deployment window ran out before anything ran.</summary>
    Expired = 15,
}

public enum TargetStatus
{
    /// <summary>Waiting to be sent (first attempt or retry).</summary>
    Pending = 1,

    /// <summary>A signed command was created for this attempt; the device has not accepted it yet.</summary>
    Dispatched = 2,

    /// <summary>The device verified and accepted the command.</summary>
    Running = 3,

    Succeeded = 10,
    Failed = 11,
    Cancelled = 12,

    /// <summary>Not attempted: duplicate of another active deployment, retired device, or blocked by policy.</summary>
    Skipped = 13,

    /// <summary>The device accepted the command but went silent past the execution timeout (interrupted).</summary>
    TimedOut = 14,

    /// <summary>The deployment window closed before the device could be reached.</summary>
    Expired = 15,
}

/// <summary>
/// A request to install or remove one package on a set of devices. The package's installation
/// parameters are frozen when the deployment is created (<see cref="Parameters"/>), so what was
/// reviewed and approved is exactly what the devices receive, even if the package is reconfigured later.
/// </summary>
public sealed class SoftwareDeployment : TenantEntity
{
    public const int MaxTargets = 5000;

    private SoftwareDeployment()
    {
    }

    internal SoftwareDeployment(Guid tenantId, Guid createdBy, DateTimeOffset createdAtUtc)
        : base(tenantId)
    {
        CreatedBy = createdBy;
        RequestedAtUtc = createdAtUtc;
        Status = DeploymentStatus.PendingApproval;
    }

    public string Name { get; internal set; } = string.Empty;

    public string? Reason { get; internal set; }

    public DeploymentAction Action { get; internal set; }

    public Guid PackageId { get; internal set; }

    public string PackageName { get; internal set; } = string.Empty;

    public string? PackageVersion { get; internal set; }

    public string PackageSha256 { get; internal set; } = string.Empty;

    public InstallerType InstallerType { get; internal set; }

    public int PackageRevision { get; internal set; }

    /// <summary>The signed-command parameters every target receives (plus its own target id).</summary>
    public Dictionary<string, string> Parameters { get; internal set; } = [];

    public Guid? DeploymentGroupId { get; internal set; }

    public DeploymentStatus Status { get; internal set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    /// <summary>Earliest time commands are sent (scheduled deployment).</summary>
    public DateTimeOffset ScheduledStartUtc { get; internal set; }

    /// <summary>After this, devices not yet reached are marked <see cref="TargetStatus.Expired"/>.</summary>
    public DateTimeOffset DeadlineUtc { get; internal set; }

    public int MaxAttempts { get; internal set; } = 3;

    public int RetryDelayMinutes { get; internal set; } = 15;

    public int TimeoutMinutes { get; internal set; } = 60;

    public bool IsSensitive { get; internal set; }

    public List<string> SensitivityReasons { get; internal set; } = [];

    /// <summary>0 (policy allows unattended deployments), 1 (standard) or 2 (sensitive).</summary>
    public int RequiredApprovals { get; internal set; }

    public DateTimeOffset ApprovalExpiresAtUtc { get; internal set; }

    public DateTimeOffset? ApprovedAtUtc { get; internal set; }

    /// <summary>The approval that completed the four-eyes (or six-eyes) check; carried in the signed command.</summary>
    public Guid? FinalApprovalId { get; internal set; }

    public Guid? FinalApprovedBy { get; internal set; }

    public DateTimeOffset? StartedAtUtc { get; internal set; }

    public DateTimeOffset? CompletedAtUtc { get; internal set; }

    public Guid? CancelledBy { get; internal set; }

    public DateTimeOffset? CancelledAtUtc { get; internal set; }

    public int TargetCount { get; internal set; }

    /// <summary>Final deployment report (JSON), generated once when the deployment finishes.</summary>
    public string? ReportJson { get; internal set; }

    public static bool IsFinal(DeploymentStatus status) => status >= DeploymentStatus.Completed;

    public bool IsFinal() => IsFinal(Status);
}

/// <summary>One device of a deployment: its current attempt, the command that carries it and the outcome.</summary>
public sealed class DeploymentTarget : TenantEntity
{
    private DeploymentTarget()
    {
    }

    internal DeploymentTarget(Guid tenantId, SoftwareDeployment deployment, Guid deviceId, string deviceName)
        : base(tenantId)
    {
        DeploymentId = deployment.Id;
        PackageId = deployment.PackageId;
        Action = deployment.Action;
        DeviceId = deviceId;
        DeviceName = deviceName;
        Status = TargetStatus.Pending;
    }

    public Guid DeploymentId { get; private set; }

    /// <summary>Denormalised for the "one active deployment per package and device" unique index.</summary>
    public Guid PackageId { get; private set; }

    public DeploymentAction Action { get; private set; }

    public Guid DeviceId { get; private set; }

    public string DeviceName { get; private set; } = string.Empty;

    public TargetStatus Status { get; internal set; }

    public int Attempts { get; internal set; }

    /// <summary>The command of the current attempt; chosen before it is dispatched (idempotency key).</summary>
    public Guid? CurrentExecutionId { get; internal set; }

    public DateTimeOffset? NextAttemptAtUtc { get; internal set; }

    public SoftwareOutcome? Outcome { get; internal set; }

    public int? ExitCode { get; internal set; }

    public string? ErrorCode { get; internal set; }

    public string? ErrorMessage { get; internal set; }

    public bool RebootRequired { get; internal set; }

    public string? DetectedVersion { get; internal set; }

    public string? LogExcerpt { get; internal set; }

    public DateTimeOffset? StartedAtUtc { get; internal set; }

    public DateTimeOffset? CompletedAtUtc { get; internal set; }

    public static bool IsActive(TargetStatus status) => status is TargetStatus.Pending or TargetStatus.Dispatched or TargetStatus.Running;

    public static bool IsFinal(TargetStatus status) => status >= TargetStatus.Succeeded;
}

public enum ApprovalDecision
{
    Approved = 1,
    Rejected = 2,
}

/// <summary>One approver's decision. The requester can never decide (constraint), and nobody decides twice.</summary>
public sealed class DeploymentApproval : TenantEntity
{
    private DeploymentApproval()
    {
    }

    internal DeploymentApproval(Guid tenantId, Guid deploymentId, Guid requestedBy, Guid approverId, ApprovalDecision decision, string? comment, DateTimeOffset decidedAtUtc)
        : base(tenantId)
    {
        DeploymentId = deploymentId;
        RequestedBy = requestedBy;
        ApproverId = approverId;
        Decision = decision;
        Comment = comment;
        DecidedAtUtc = decidedAtUtc;
    }

    public Guid DeploymentId { get; private set; }

    public Guid RequestedBy { get; private set; }

    public Guid ApproverId { get; private set; }

    public ApprovalDecision Decision { get; private set; }

    public string? Comment { get; private set; }

    public DateTimeOffset DecidedAtUtc { get; private set; }
}

/// <summary>
/// A reusable set of deployment targets: explicit devices, device groups and branches, minus
/// excluded devices. Resolved to concrete devices when a deployment is created.
/// </summary>
public sealed class DeploymentGroup : TenantEntity
{
    private DeploymentGroup()
    {
    }

    internal DeploymentGroup(Guid tenantId)
        : base(tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public List<Guid> DeviceIds { get; private set; } = [];

    public List<Guid> DeviceGroupIds { get; private set; } = [];

    public List<Guid> BranchIds { get; private set; } = [];

    public List<Guid> ExcludedDeviceIds { get; private set; } = [];

    public void Update(string name, string? description, IEnumerable<Guid> deviceIds, IEnumerable<Guid> deviceGroupIds, IEnumerable<Guid> branchIds, IEnumerable<Guid> excludedDeviceIds)
    {
        Name = name;
        Description = description;
        DeviceIds = deviceIds.Distinct().ToList();
        DeviceGroupIds = deviceGroupIds.Distinct().ToList();
        BranchIds = branchIds.Distinct().ToList();
        ExcludedDeviceIds = excludedDeviceIds.Distinct().ToList();
    }
}
