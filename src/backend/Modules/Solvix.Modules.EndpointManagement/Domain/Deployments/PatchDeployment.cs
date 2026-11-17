namespace Solvix.Modules.EndpointManagement.Domain.Deployments;

using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.EndpointManagement.Domain.Targeting;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

public enum DeploymentStatus
{
    /// <summary>Being prepared; nothing is sent to devices.</summary>
    Draft = 1,

    /// <summary>Authorised by an administrator; starts at <see cref="PatchDeployment.ScheduledStartUtc"/>.</summary>
    Scheduled = 2,
    InProgress = 3,

    /// <summary>Pilot ring met the success threshold; waiting for an administrator to promote to all targets.</summary>
    AwaitingPromotion = 4,

    /// <summary>Stopped automatically (pilot or failure threshold breached); needs an administrator decision.</summary>
    Halted = 5,

    /// <summary>Stopped by an administrator; can be resumed.</summary>
    Paused = 6,
    Completed = 7,
    CompletedWithFailures = 8,
    Cancelled = 9,
}

public enum DeploymentPhase
{
    NotStarted = 0,
    Pilot = 1,
    Broad = 2,
}

/// <summary>
/// A controlled rollout of approved patches to a set of devices: optional pilot
/// ring with a success gate, maintenance-window aware dispatch, automatic halt
/// when the failure rate exceeds a threshold, retries for interrupted or failed
/// installations, and restart handling.
/// </summary>
public sealed class PatchDeployment : TenantEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int ReasonMaxLength = 1000;

    private readonly List<PatchDeploymentPatch> _patches = [];
    private readonly List<PatchDeploymentTarget> _targets = [];

    private PatchDeployment()
    {
    }

    private PatchDeployment(Guid tenantId, string name, Guid createdByUserId)
        : base(tenantId)
    {
        Name = name;
        CreatedByUserId = createdByUserId;
        Status = DeploymentStatus.Draft;
        Phase = DeploymentPhase.NotStarted;
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public DeploymentStatus Status { get; private set; }

    public DeploymentPhase Phase { get; private set; }

    public DateTimeOffset ScheduledStartUtc { get; private set; }

    /// <summary>After the deadline no new installation starts; undispatched devices time out.</summary>
    public DateTimeOffset? DeadlineUtc { get; private set; }

    public Guid? MaintenanceWindowId { get; private set; }

    public RestartBehavior RestartBehavior { get; private set; } = RestartBehavior.WithinMaintenanceWindow;

    /// <summary>Warning shown to signed-in users before an automatic restart.</summary>
    public int RestartGracePeriodSeconds { get; private set; } = 600;

    public Guid? PilotDeviceGroupId { get; private set; }

    /// <summary>Alternative to a pilot group: a deterministic sample of the targets.</summary>
    public int? PilotPercentage { get; private set; }

    public int PilotSuccessThresholdPercent { get; private set; } = 90;

    public int PilotSoakMinutes { get; private set; }

    public bool AutoPromote { get; private set; }

    /// <summary>Halt the broad phase when this share of finished devices failed.</summary>
    public int FailureThresholdPercent { get; private set; } = 20;

    public int MaxAttempts { get; private set; } = 3;

    public Guid CreatedByUserId { get; private set; }

    public Guid? AuthorizedByUserId { get; private set; }

    public DateTimeOffset? AuthorizedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? PilotCompletedAtUtc { get; private set; }

    public DateTimeOffset? PromotedAtUtc { get; private set; }

    public Guid? PromotedByUserId { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? HaltReason { get; private set; }

    public uint RowVersion { get; private set; }

    public IReadOnlyCollection<PatchDeploymentPatch> Patches => _patches;

    public IReadOnlyCollection<PatchDeploymentTarget> Targets => _targets;

    public bool HasPilot => PilotDeviceGroupId is not null || PilotPercentage is > 0;

    public bool IsTerminal => Status is DeploymentStatus.Completed or DeploymentStatus.CompletedWithFailures or DeploymentStatus.Cancelled;

    /// <summary>True while devices may receive installation jobs.</summary>
    public bool IsDispatching => Status == DeploymentStatus.InProgress;

    public static PatchDeployment Create(Guid tenantId, string name, Guid createdByUserId) =>
        new(tenantId, Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength), createdByUserId);

    public void Configure(
        string name,
        string? description,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset? deadlineUtc,
        Guid? maintenanceWindowId,
        RestartBehavior restartBehavior,
        int restartGracePeriodSeconds,
        Guid? pilotDeviceGroupId,
        int? pilotPercentage,
        int pilotSuccessThresholdPercent,
        int pilotSoakMinutes,
        bool autoPromote,
        int failureThresholdPercent,
        int maxAttempts)
    {
        RequireStatus(DeploymentStatus.Draft, "Only draft deployments can be edited.");
        if (deadlineUtc is { } deadline && deadline <= scheduledStartUtc)
        {
            throw new ValidationException("deadlineUtc", "The deadline must be after the scheduled start.");
        }

        if (pilotDeviceGroupId is not null && pilotPercentage is not null)
        {
            throw new ValidationException("pilotPercentage", "Use either a pilot device group or a pilot percentage, not both.");
        }

        if (restartBehavior == RestartBehavior.ForceAtDeadline && deadlineUtc is null)
        {
            throw new ValidationException("deadlineUtc", "ForceAtDeadline restarts need a deadline.");
        }

        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        Description = Guard.Optional(description, "description", DescriptionMaxLength);
        ScheduledStartUtc = scheduledStartUtc.ToUniversalTime();
        DeadlineUtc = deadlineUtc?.ToUniversalTime();
        MaintenanceWindowId = maintenanceWindowId;
        RestartBehavior = restartBehavior;
        RestartGracePeriodSeconds = Math.Clamp(restartGracePeriodSeconds, 0, 86_400);
        PilotDeviceGroupId = pilotDeviceGroupId;
        PilotPercentage = pilotPercentage;
        PilotSuccessThresholdPercent = Math.Clamp(pilotSuccessThresholdPercent, 1, 100);
        PilotSoakMinutes = Math.Clamp(pilotSoakMinutes, 0, 60 * 24 * 14);
        AutoPromote = autoPromote;
        FailureThresholdPercent = Math.Clamp(failureThresholdPercent, 1, 100);
        MaxAttempts = Math.Clamp(maxAttempts, 1, 10);
    }

    public void SetPatches(IEnumerable<Guid> patchIds)
    {
        ArgumentNullException.ThrowIfNull(patchIds);
        RequireStatus(DeploymentStatus.Draft, "Only draft deployments can be edited.");
        _patches.Clear();
        foreach (var id in patchIds.Distinct())
        {
            _patches.Add(new PatchDeploymentPatch(TenantId, Id, id));
        }
    }

    public void SetTargets(IEnumerable<TargetRef> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        RequireStatus(DeploymentStatus.Draft, "Only draft deployments can be edited.");
        _targets.Clear();
        foreach (var t in targets.Distinct())
        {
            _targets.Add(new PatchDeploymentTarget(TenantId, Id, t.Type, t.Id, t.IsExclusion));
        }
    }

    /// <summary>The explicit administrator decision that allows devices to be changed.</summary>
    public void Authorize(Guid userId, DateTimeOffset now)
    {
        RequireStatus(DeploymentStatus.Draft, "Only draft deployments can be scheduled.");
        if (_patches.Count == 0)
        {
            throw new ValidationException("patchIds", "Select at least one patch.");
        }

        if (!_targets.Any(t => !t.IsExclusion))
        {
            throw new ValidationException("targets", "Select at least one target.");
        }

        if (DeadlineUtc is { } deadline && deadline <= now)
        {
            throw new ValidationException("deadlineUtc", "The deadline has already passed.");
        }

        Status = DeploymentStatus.Scheduled;
        AuthorizedByUserId = userId;
        AuthorizedAtUtc = now;
    }

    public void Unschedule()
    {
        RequireStatus(DeploymentStatus.Scheduled, "Only scheduled deployments that have not started can be returned to draft.");
        Status = DeploymentStatus.Draft;
        AuthorizedByUserId = null;
        AuthorizedAtUtc = null;
    }

    public void Start(DateTimeOffset now, bool withPilot)
    {
        RequireStatus(DeploymentStatus.Scheduled, "The deployment is not scheduled.");
        Status = DeploymentStatus.InProgress;
        Phase = withPilot ? DeploymentPhase.Pilot : DeploymentPhase.Broad;
        StartedAtUtc = now;
        if (!withPilot)
        {
            PromotedAtUtc = now;
        }
    }

    public void PilotPassed(DateTimeOffset now)
    {
        PilotCompletedAtUtc ??= now;
        if (!AutoPromote)
        {
            Status = DeploymentStatus.AwaitingPromotion;
        }
    }

    public bool SoakElapsed(DateTimeOffset now) =>
        PilotCompletedAtUtc is { } done && now >= done.AddMinutes(PilotSoakMinutes);

    /// <summary>Moves from the pilot ring to all targets. Forcing past a failed pilot is an explicit, audited override.</summary>
    public void Promote(Guid? userId, DateTimeOffset now, bool force)
    {
        if (Phase != DeploymentPhase.Pilot)
        {
            throw new ConflictException("Only deployments in the pilot phase can be promoted.");
        }

        var allowed = Status is DeploymentStatus.AwaitingPromotion or DeploymentStatus.InProgress
            || (force && Status == DeploymentStatus.Halted);
        if (!allowed)
        {
            throw new ConflictException(Status == DeploymentStatus.Halted
                ? "The pilot failed its success threshold; promoting requires force=true and a reason."
                : $"A {Status} deployment cannot be promoted.");
        }

        Phase = DeploymentPhase.Broad;
        Status = DeploymentStatus.InProgress;
        PilotCompletedAtUtc ??= now;
        PromotedAtUtc = now;
        PromotedByUserId = userId;
        HaltReason = null;
    }

    public void Halt(string reason)
    {
        if (Status != DeploymentStatus.InProgress)
        {
            return;
        }

        Status = DeploymentStatus.Halted;
        HaltReason = Truncate(reason);
    }

    public void Pause(string? reason)
    {
        if (Status is not (DeploymentStatus.InProgress or DeploymentStatus.AwaitingPromotion))
        {
            throw new ConflictException($"A {Status} deployment cannot be paused.");
        }

        Status = DeploymentStatus.Paused;
        HaltReason = Truncate(reason ?? "Paused by an administrator.");
    }

    /// <summary>Resumes after a pause or an automatic halt in the broad phase (halts in the pilot need a promotion decision).</summary>
    public void Resume()
    {
        if (Status == DeploymentStatus.Paused || (Status == DeploymentStatus.Halted && Phase == DeploymentPhase.Broad))
        {
            Status = DeploymentStatus.InProgress;
            HaltReason = null;
            return;
        }

        throw new ConflictException(Status == DeploymentStatus.Halted
            ? "A halted pilot is resumed by promoting it (with force) or cancelled."
            : $"A {Status} deployment cannot be resumed.");
    }

    public void Cancel(DateTimeOffset now, string? reason)
    {
        if (IsTerminal)
        {
            throw new ConflictException($"The deployment is already {Status}.");
        }

        Status = DeploymentStatus.Cancelled;
        CompletedAtUtc = now;
        HaltReason = Truncate(reason ?? "Cancelled by an administrator.");
    }

    public void Complete(bool withFailures, DateTimeOffset now)
    {
        Status = withFailures ? DeploymentStatus.CompletedWithFailures : DeploymentStatus.Completed;
        CompletedAtUtc = now;
    }

    private void RequireStatus(DeploymentStatus status, string message)
    {
        if (Status != status)
        {
            throw new ConflictException(message);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= ReasonMaxLength ? value : value[..ReasonMaxLength];
}

public sealed class PatchDeploymentPatch : TenantEntity
{
    private PatchDeploymentPatch()
    {
    }

    public PatchDeploymentPatch(Guid tenantId, Guid deploymentId, Guid patchId)
        : base(tenantId)
    {
        DeploymentId = deploymentId;
        PatchId = patchId;
    }

    public Guid DeploymentId { get; private set; }

    public Guid PatchId { get; private set; }
}

public sealed class PatchDeploymentTarget : TenantEntity
{
    private PatchDeploymentTarget()
    {
    }

    public PatchDeploymentTarget(Guid tenantId, Guid deploymentId, TargetType targetType, Guid? targetId, bool isExclusion)
        : base(tenantId)
    {
        DeploymentId = deploymentId;
        TargetType = targetType;
        TargetId = targetType == TargetType.AllDevices ? null : targetId;
        IsExclusion = isExclusion;
    }

    public Guid DeploymentId { get; private set; }

    public TargetType TargetType { get; private set; }

    public Guid? TargetId { get; private set; }

    public bool IsExclusion { get; private set; }

    public TargetRef ToRef() => new(TargetType, TargetId, IsExclusion);
}
