namespace Solvix.Modules.EndpointManagement.Application.Deployments;

using FluentValidation;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.Targeting;

public sealed record TargetDto(TargetType TargetType, Guid? TargetId, bool IsExclusion = false)
{
    public TargetRef ToRef() => new(TargetType, TargetType == TargetType.AllDevices ? null : TargetId, IsExclusion);
}

public sealed record SaveDeploymentRequest(
    string Name,
    string? Description,
    IReadOnlyList<Guid> PatchIds,
    IReadOnlyList<TargetDto> Targets,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset? DeadlineUtc,
    Guid? MaintenanceWindowId,
    RestartBehavior RestartBehavior = RestartBehavior.WithinMaintenanceWindow,
    int RestartGracePeriodSeconds = 600,
    Guid? PilotDeviceGroupId = null,
    int? PilotPercentage = null,
    int PilotSuccessThresholdPercent = 90,
    int PilotSoakMinutes = 0,
    bool AutoPromote = false,
    int FailureThresholdPercent = 20,
    int MaxAttempts = 3);

public sealed record DeploymentActionRequest(string? Reason, bool Force = false);

public sealed record DeploymentProgressDto(
    int Devices,
    int Pending,
    int InFlight,
    int PendingRestart,
    int Succeeded,
    int Failed,
    int NotApplicable,
    int TimedOut,
    int Cancelled,
    int PilotDevices,
    int PilotSettled,
    double? PilotSuccessPercent,
    double? FailurePercent);

public sealed record DeploymentDto(
    Guid Id,
    string Name,
    string? Description,
    DeploymentStatus Status,
    DeploymentPhase Phase,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset? DeadlineUtc,
    Guid? MaintenanceWindowId,
    RestartBehavior RestartBehavior,
    int RestartGracePeriodSeconds,
    Guid? PilotDeviceGroupId,
    int? PilotPercentage,
    int PilotSuccessThresholdPercent,
    int PilotSoakMinutes,
    bool AutoPromote,
    int FailureThresholdPercent,
    int MaxAttempts,
    IReadOnlyList<Guid> PatchIds,
    IReadOnlyList<TargetDto> Targets,
    Guid CreatedByUserId,
    Guid? AuthorizedByUserId,
    DateTimeOffset? AuthorizedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? PilotCompletedAtUtc,
    DateTimeOffset? PromotedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? HaltReason,
    DeploymentProgressDto Progress,
    DateTimeOffset CreatedAtUtc);

public sealed record DeploymentJobDto(
    Guid Id,
    Guid DeviceId,
    string Hostname,
    DeploymentRing Ring,
    PatchJobStatus Status,
    int Attempt,
    int FailureCount,
    PatchJobStage? Stage,
    int PercentComplete,
    string? Message,
    string? LastError,
    string? LastHResultHex,
    bool RebootRequired,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? LastProgressAtUtc,
    DateTimeOffset? LeaseExpiresUtc,
    DateTimeOffset? NextAttemptAfterUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record DeploymentListQuery(DeploymentStatus? Status, int Page = 1, int PageSize = 25);

public sealed record RestartRequestDto(
    Guid Id,
    Guid DeviceId,
    string Hostname,
    RestartSource Source,
    Guid? DeploymentId,
    string Reason,
    RestartRequestStatus Status,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset? DeadlineUtc,
    Guid? MaintenanceWindowId,
    bool Force,
    int GracePeriodSeconds,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? InitiatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Message,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateRestartRequest(
    Guid DeviceId,
    string Reason,
    DateTimeOffset? NotBeforeUtc,
    DateTimeOffset? DeadlineUtc,
    Guid? MaintenanceWindowId,
    bool Force = false,
    int GracePeriodSeconds = 600);

public sealed record RestartListQuery(Guid? DeviceId, bool OpenOnly = true, int Page = 1, int PageSize = 50);

public sealed class SaveDeploymentRequestValidator : AbstractValidator<SaveDeploymentRequest>
{
    public SaveDeploymentRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(PatchDeployment.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(PatchDeployment.DescriptionMaxLength);
        RuleFor(x => x.PatchIds).NotNull().Must(p => p.Count is > 0 and <= 200).WithMessage("Select between 1 and 200 patches.");
        RuleFor(x => x.Targets).NotNull().Must(t => t.Count is > 0 and <= 200 && t.Any(x => !x.IsExclusion))
            .WithMessage("Provide at least one (and at most 200) targets including one that is not an exclusion.");
        RuleForEach(x => x.Targets).ChildRules(t =>
        {
            t.RuleFor(x => x.TargetType).IsInEnum();
            t.RuleFor(x => x.TargetId).NotNull().When(x => x.TargetType != TargetType.AllDevices).WithMessage("targetId is required unless the target is AllDevices.");
        });
        RuleFor(x => x.DeadlineUtc).GreaterThan(x => x.ScheduledStartUtc).When(x => x.DeadlineUtc is not null)
            .WithMessage("The deadline must be after the scheduled start.");
        RuleFor(x => x.RestartBehavior).IsInEnum();
        RuleFor(x => x.DeadlineUtc).NotNull().When(x => x.RestartBehavior == RestartBehavior.ForceAtDeadline)
            .WithMessage("ForceAtDeadline restarts need a deadline.");
        RuleFor(x => x.RestartGracePeriodSeconds).InclusiveBetween(0, 86_400);
        RuleFor(x => x.PilotPercentage).InclusiveBetween(1, 100).When(x => x.PilotPercentage is not null);
        RuleFor(x => x.PilotPercentage).Null().When(x => x.PilotDeviceGroupId is not null)
            .WithMessage("Use either a pilot device group or a pilot percentage, not both.");
        RuleFor(x => x.PilotSuccessThresholdPercent).InclusiveBetween(1, 100);
        RuleFor(x => x.PilotSoakMinutes).InclusiveBetween(0, 60 * 24 * 14);
        RuleFor(x => x.FailureThresholdPercent).InclusiveBetween(1, 100);
        RuleFor(x => x.MaxAttempts).InclusiveBetween(1, 10);
    }
}

public sealed class CreateRestartRequestValidator : AbstractValidator<CreateRestartRequest>
{
    public CreateRestartRequestValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(DeviceRestartRequest.ReasonMaxLength);
        RuleFor(x => x.GracePeriodSeconds).InclusiveBetween(0, 86_400);
        RuleFor(x => x.DeadlineUtc).NotNull().When(x => x.Force).WithMessage("A forced restart needs a deadline after which the window may be ignored.");
    }
}
