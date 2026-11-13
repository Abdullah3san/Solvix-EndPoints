namespace Solvix.Modules.EndpointManagement.Domain.Deployments;

using Solvix.Endpoint.Contracts.Patching;
using Solvix.SharedKernel.Entities;

public enum DeploymentRing
{
    Pilot = 1,
    Broad = 2,
}

public enum PatchJobStatus
{
    /// <summary>Waiting for its ring, the maintenance window, the start time or a retry delay.</summary>
    Pending = 1,

    /// <summary>Handed to the agent; the lease must be renewed by progress reports.</summary>
    Dispatched = 2,
    InProgress = 3,

    /// <summary>Installed; waiting for the device to restart and confirm.</summary>
    PendingRestart = 4,
    Succeeded = 5,
    Failed = 6,

    /// <summary>All patches already installed or not offered to the device.</summary>
    NotApplicable = 7,

    /// <summary>The deadline passed before the device installed the patches.</summary>
    TimedOut = 8,
    Cancelled = 9,
}

/// <summary>
/// The installation of a deployment's patches on one device. The agent holds a
/// lease while it works; if the lease expires without a result (agent crash,
/// power loss, unexpected reboot mid-install) the job is treated as interrupted
/// and retried with back-off until <see cref="PatchDeployment.MaxAttempts"/>.
/// Each attempt is numbered so late results from an abandoned attempt are ignored.
/// </summary>
public sealed class PatchDeploymentJob : TenantEntity
{
    public const int MessageMaxLength = 1000;

    private PatchDeploymentJob()
    {
    }

    public PatchDeploymentJob(Guid tenantId, Guid deploymentId, Guid deviceId, DeploymentRing ring)
        : base(tenantId)
    {
        DeploymentId = deploymentId;
        DeviceId = deviceId;
        Ring = ring;
        Status = PatchJobStatus.Pending;
    }

    public Guid DeploymentId { get; private set; }

    public Guid DeviceId { get; private set; }

    public DeploymentRing Ring { get; private set; }

    public PatchJobStatus Status { get; private set; }

    /// <summary>Dispatch counter; identifies the attempt so late results of an abandoned attempt are rejected.</summary>
    public int Attempt { get; private set; }

    /// <summary>Failed or interrupted attempts; compared with the deployment's MaxAttempts.</summary>
    public int FailureCount { get; private set; }

    public DateTimeOffset? LeaseExpiresUtc { get; private set; }

    public DateTimeOffset? NextAttemptAfterUtc { get; private set; }

    public DateTimeOffset? DispatchedAtUtc { get; private set; }

    public DateTimeOffset? LastProgressAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public PatchJobStage? Stage { get; private set; }

    public int PercentComplete { get; private set; }

    public string? Message { get; private set; }

    public string? LastError { get; private set; }

    public int? LastHResult { get; private set; }

    public bool RebootRequired { get; private set; }

    /// <summary>Per-update results of the last attempt (JSON array of PatchUpdateResult).</summary>
    public string? ResultsJson { get; private set; }

    public uint RowVersion { get; private set; }

    public bool IsTerminal => Status is PatchJobStatus.Succeeded or PatchJobStatus.Failed or PatchJobStatus.NotApplicable
        or PatchJobStatus.TimedOut or PatchJobStatus.Cancelled;

    /// <summary>Terminal for rollout decisions: a device pending restart has installed the patches.</summary>
    public bool IsSettled => IsTerminal || Status == PatchJobStatus.PendingRestart;

    public bool IsInFlight => Status is PatchJobStatus.Dispatched or PatchJobStatus.InProgress;

    public bool CountsAsSuccess => Status is PatchJobStatus.Succeeded or PatchJobStatus.PendingRestart;

    public bool CountsAsFailure => Status is PatchJobStatus.Failed or PatchJobStatus.TimedOut;

    public bool IsEligible(DateTimeOffset now) =>
        Status == PatchJobStatus.Pending && (NextAttemptAfterUtc is null || NextAttemptAfterUtc <= now);

    public void Dispatch(DateTimeOffset now, TimeSpan lease)
    {
        Attempt++;
        Status = PatchJobStatus.Dispatched;
        DispatchedAtUtc = now;
        LeaseExpiresUtc = now + lease;
        NextAttemptAfterUtc = null;
        Stage = null;
        PercentComplete = 0;
        Message = null;
    }

    public void ReportProgress(PatchJobStage stage, int percent, string? message, DateTimeOffset now, TimeSpan lease)
    {
        Status = PatchJobStatus.InProgress;
        Stage = stage;
        PercentComplete = Math.Clamp(percent, 0, 100);
        Message = Clip(message);
        LastProgressAtUtc = now;
        LeaseExpiresUtc = now + lease;
    }

    public void Succeed(bool rebootRequired, string? resultsJson, DateTimeOffset now)
    {
        RebootRequired = rebootRequired;
        ResultsJson = resultsJson;
        LeaseExpiresUtc = null;
        PercentComplete = 100;
        if (rebootRequired)
        {
            Status = PatchJobStatus.PendingRestart;
            Message = "Installed; waiting for restart.";
        }
        else
        {
            Status = PatchJobStatus.Succeeded;
            CompletedAtUtc = now;
            Message = "Installed.";
        }
    }

    /// <summary>
    /// Records a failed or interrupted attempt. Retries are scheduled with linear
    /// back-off while attempts remain; otherwise the job fails permanently.
    /// </summary>
    public void FailAttempt(string error, int? hresult, string? resultsJson, int maxAttempts, DateTimeOffset now, TimeSpan retryDelay)
    {
        FailureCount++;
        LastError = Clip(error);
        LastHResult = hresult;
        ResultsJson = resultsJson ?? ResultsJson;
        LeaseExpiresUtc = null;
        if (FailureCount < maxAttempts)
        {
            Status = PatchJobStatus.Pending;
            NextAttemptAfterUtc = now + (retryDelay * FailureCount);
            Message = $"Attempt {Attempt} failed; retry {FailureCount + 1} of {maxAttempts} scheduled.";
        }
        else
        {
            Status = PatchJobStatus.Failed;
            CompletedAtUtc = now;
            Message = $"Failed after {FailureCount} attempt(s).";
        }
    }

    /// <summary>
    /// The agent stopped cleanly without finishing (maintenance window closed,
    /// service shutting down). Does not count as a failure; retried later.
    /// </summary>
    public void Requeue(string reason, DateTimeOffset notBefore)
    {
        Status = PatchJobStatus.Pending;
        LeaseExpiresUtc = null;
        NextAttemptAfterUtc = notBefore;
        Message = Clip(reason);
    }

    public void MarkNotApplicable(string message, DateTimeOffset now)
    {
        Status = PatchJobStatus.NotApplicable;
        Message = Clip(message);
        LeaseExpiresUtc = null;
        CompletedAtUtc = now;
    }

    /// <summary>The device restarted and a rescan shows every patch installed.</summary>
    public void ConfirmRestart(DateTimeOffset now)
    {
        if (Status != PatchJobStatus.PendingRestart)
        {
            return;
        }

        Status = PatchJobStatus.Succeeded;
        RebootRequired = false;
        CompletedAtUtc = now;
        Message = "Installed and restarted.";
    }

    /// <summary>After the restart a rescan shows a patch still missing: the installation did not survive the restart.</summary>
    public void FailAfterRestart(string error, DateTimeOffset now)
    {
        Status = PatchJobStatus.Failed;
        LastError = Clip(error);
        CompletedAtUtc = now;
    }

    public void TimeOut(DateTimeOffset now)
    {
        Status = PatchJobStatus.TimedOut;
        LeaseExpiresUtc = null;
        CompletedAtUtc = now;
        Message = "Deadline passed before installation.";
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = PatchJobStatus.Cancelled;
        LeaseExpiresUtc = null;
        CompletedAtUtc = now;
    }

    private static string? Clip(string? value) =>
        value is null ? null : value.Length <= MessageMaxLength ? value : value[..MessageMaxLength];
}

public enum RestartRequestStatus
{
    Pending = 1,
    Dispatched = 2,

    /// <summary>The agent started the restart countdown.</summary>
    Initiated = 3,

    /// <summary>The device came back with a newer boot time.</summary>
    Completed = 4,
    Cancelled = 5,
    Failed = 6,
}

public enum RestartSource
{
    Deployment = 1,
    Manual = 2,
}

/// <summary>
/// A request to restart one device, either because a deployment installed patches
/// that need it or because an administrator asked. Restarts honour the
/// maintenance window unless <see cref="Force"/> is set and the deadline passed.
/// </summary>
public sealed class DeviceRestartRequest : TenantEntity
{
    public const int ReasonMaxLength = 500;

    private DeviceRestartRequest()
    {
    }

    public DeviceRestartRequest(
        Guid tenantId,
        Guid deviceId,
        RestartSource source,
        Guid? deploymentId,
        string reason,
        DateTimeOffset notBeforeUtc,
        DateTimeOffset? deadlineUtc,
        Guid? maintenanceWindowId,
        bool force,
        int gracePeriodSeconds,
        Guid? requestedByUserId)
        : base(tenantId)
    {
        DeviceId = deviceId;
        Source = source;
        DeploymentId = deploymentId;
        Reason = reason.Length <= ReasonMaxLength ? reason : reason[..ReasonMaxLength];
        NotBeforeUtc = notBeforeUtc;
        DeadlineUtc = deadlineUtc;
        MaintenanceWindowId = maintenanceWindowId;
        Force = force;
        GracePeriodSeconds = Math.Clamp(gracePeriodSeconds, 0, 86_400);
        RequestedByUserId = requestedByUserId;
        Status = RestartRequestStatus.Pending;
    }

    public Guid DeviceId { get; private set; }

    public RestartSource Source { get; private set; }

    public Guid? DeploymentId { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset NotBeforeUtc { get; private set; }

    public DateTimeOffset? DeadlineUtc { get; private set; }

    public Guid? MaintenanceWindowId { get; private set; }

    /// <summary>Restart outside the maintenance window once the deadline has passed.</summary>
    public bool Force { get; private set; }

    public int GracePeriodSeconds { get; private set; }

    public Guid? RequestedByUserId { get; private set; }

    public RestartRequestStatus Status { get; private set; }

    public DateTimeOffset? DispatchedAtUtc { get; private set; }

    public DateTimeOffset? InitiatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? Message { get; private set; }

    public bool IsOpen => Status is RestartRequestStatus.Pending or RestartRequestStatus.Dispatched or RestartRequestStatus.Initiated;

    public void Dispatch(DateTimeOffset now)
    {
        Status = RestartRequestStatus.Dispatched;
        DispatchedAtUtc = now;
    }

    public void Acknowledge(RestartAckStatus ack, string? message, DateTimeOffset now)
    {
        Message = message is { Length: > 500 } ? message[..500] : message;
        switch (ack)
        {
            case RestartAckStatus.Initiated:
                Status = RestartRequestStatus.Initiated;
                InitiatedAtUtc = now;
                break;
            case RestartAckStatus.Postponed:
                Status = RestartRequestStatus.Pending;
                break;
            default:
                Status = RestartRequestStatus.Failed;
                CompletedAtUtc = now;
                break;
        }
    }

    public void Complete(DateTimeOffset now)
    {
        Status = RestartRequestStatus.Completed;
        CompletedAtUtc = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = RestartRequestStatus.Cancelled;
        CompletedAtUtc = now;
    }
}
