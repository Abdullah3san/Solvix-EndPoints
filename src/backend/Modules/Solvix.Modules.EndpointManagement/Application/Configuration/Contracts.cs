namespace Solvix.Modules.EndpointManagement.Application.Configuration;

using FluentValidation;
using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.Modules.EndpointManagement.Domain.Targeting;

public sealed record ConfigurationPolicySummaryDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsArchived,
    int? ActiveVersion,
    SettingRisk? ActiveVersionRisk,
    int LatestVersion,
    PolicyVersionStatus LatestVersionStatus,
    int AssignmentCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record SettingView(
    string Key,
    SettingKind Kind,
    ConfigurationSetting Setting,
    SettingRisk Risk,
    string RiskReason,
    string Description);

public sealed record PolicyVersionDto(
    Guid Id,
    Guid PolicyId,
    int Number,
    PolicyVersionStatus Status,
    SettingRisk MaxRisk,
    SettingRisk? AuthorizedRisk,
    string ContentHash,
    string? ChangeNote,
    int? RestoredFromNumber,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    Guid? SubmittedByUserId,
    DateTimeOffset? SubmittedAtUtc,
    Guid? ReviewedByUserId,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewNote,
    bool DestructiveAcknowledged,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? SupersededAtUtc,
    IReadOnlyList<SettingView> Settings)
{
    /// <summary>Approval needed for this version, given its highest risk.</summary>
    public string ApprovalRequirement => MaxRisk switch
    {
        SettingRisk.Low => "Any administrator (including the author).",
        SettingRisk.Elevated => "A second administrator (not the author).",
        _ => "A workspace Owner other than the author, with explicit acknowledgement of destructive changes.",
    };

    public static PolicyVersionDto From(ConfigurationPolicyVersion v) => new(
        v.Id, v.PolicyId, v.Number, v.Status, v.MaxRisk, v.AuthorizedRisk, v.ContentHash, v.ChangeNote, v.RestoredFromNumber,
        v.CreatedByUserId, v.CreatedAtUtc, v.SubmittedByUserId, v.SubmittedAtUtc, v.ReviewedByUserId, v.ReviewedAtUtc, v.ReviewNote,
        v.DestructiveAcknowledged, v.PublishedAtUtc, v.SupersededAtUtc,
        v.GetSettings().Select(s => new SettingView(s.Key, s.Kind, s, SettingRiskClassifier.Classify(s), SettingRiskClassifier.Explain(s), s.Describe())).ToList());
}

public sealed record AssignmentDto(
    Guid Id,
    Guid PolicyId,
    TargetType TargetType,
    Guid? TargetId,
    bool IsExclusion,
    EnforcementMode Mode,
    int Priority,
    Guid? MaintenanceWindowId,
    RemovalBehavior RemovalBehavior,
    DateTimeOffset CreatedAtUtc)
{
    public static AssignmentDto From(ConfigurationAssignment a) => new(
        a.Id, a.PolicyId, a.TargetType, a.TargetId, a.IsExclusion, a.Mode, a.Priority, a.MaintenanceWindowId, a.RemovalBehavior, a.CreatedAtUtc);
}

public sealed record ConfigurationPolicyDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsArchived,
    Guid? ActiveVersionId,
    IReadOnlyList<PolicyVersionDto> Versions,
    IReadOnlyList<AssignmentDto> Assignments);

public sealed record CreateConfigurationPolicyRequest(string Name, string? Description, IReadOnlyList<ConfigurationSetting> Settings, string? ChangeNote);

public sealed record UpdateConfigurationPolicyRequest(string Name, string? Description);

/// <summary>Creates a new draft; when <see cref="Settings"/> is null the active version is copied.</summary>
public sealed record CreatePolicyVersionRequest(IReadOnlyList<ConfigurationSetting>? Settings, string? ChangeNote);

public sealed record UpdatePolicyVersionRequest(IReadOnlyList<ConfigurationSetting> Settings, string? ChangeNote);

public sealed record ReviewPolicyVersionRequest(string? Note, bool AcknowledgeDestructive = false);

public sealed record SaveAssignmentRequest(
    TargetType TargetType,
    Guid? TargetId,
    bool IsExclusion,
    EnforcementMode Mode = EnforcementMode.Enforce,
    int Priority = 100,
    Guid? MaintenanceWindowId = null,
    RemovalBehavior RemovalBehavior = RemovalBehavior.Revert);

public sealed record UpdateAssignmentRequest(EnforcementMode Mode, int Priority, Guid? MaintenanceWindowId, RemovalBehavior RemovalBehavior);

public sealed record ConflictCandidateDto(Guid PolicyId, string PolicyName, int VersionNumber, Guid AssignmentId, int Priority, TargetType TargetType, string Description);

public sealed record ConflictDto(string Key, bool IsResolved, Guid? WinningPolicyId, string Reason, IReadOnlyList<ConflictCandidateDto> Candidates);

public sealed record DeviceConflictsDto(Guid DeviceId, string Hostname, IReadOnlyList<ConflictDto> Conflicts);

public sealed record EffectiveSettingDto(
    string Key,
    string Description,
    Guid PolicyId,
    string PolicyName,
    int VersionNumber,
    EnforcementMode Mode,
    SettingRisk AuthorizedRisk,
    Guid? MaintenanceWindowId,
    SettingComplianceStatus? Status,
    string? ActualValue,
    DateTimeOffset? LastEvaluatedAtUtc);

public sealed record DeviceConfigurationDto(
    Guid DeviceId,
    string Hostname,
    IReadOnlyList<EffectiveSettingDto> Settings,
    IReadOnlyList<ConflictDto> Conflicts,
    IReadOnlyList<DeviceStateDto> RemovedSettings);

public sealed record DeviceStateDto(
    string Key,
    SettingComplianceStatus? Status,
    RemovalBehavior RemovalBehavior,
    bool NeedsRevert,
    string? ActualValue,
    string? Message,
    DateTimeOffset? LastEvaluatedAtUtc);

public sealed record DriftEventDto(
    Guid Id,
    Guid DeviceId,
    string SettingKey,
    Guid? PolicyVersionId,
    SettingComplianceStatus Status,
    EnforcementMode? Mode,
    string? ExpectedValue,
    string? ActualValue,
    DateTimeOffset DetectedAtUtc);

public sealed record ConfigurationComplianceDto(
    int DevicesEvaluated,
    int DevicesCompliant,
    int DevicesWithDrift,
    int DevicesWithErrors,
    int DevicesPending,
    double CompliancePercent,
    IReadOnlyDictionary<string, int> SettingsByStatus,
    IReadOnlyList<PolicyComplianceDto> Policies);

public sealed record PolicyComplianceDto(Guid PolicyId, string PolicyName, int Devices, int Compliant, int Drifted, int Failed, int Pending);

public sealed record BaselineDto(string Id, string Name, string Description, IReadOnlyList<SettingView> Settings);

public sealed record CreateFromBaselineRequest(string BaselineId, string Name, string? Description);

public sealed class CreateConfigurationPolicyRequestValidator : AbstractValidator<CreateConfigurationPolicyRequest>
{
    public CreateConfigurationPolicyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(ConfigurationPolicy.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(ConfigurationPolicy.DescriptionMaxLength);
        RuleFor(x => x.Settings).NotNull();
        RuleFor(x => x.ChangeNote).MaximumLength(ConfigurationPolicyVersion.NoteMaxLength);
    }
}

public sealed class UpdateConfigurationPolicyRequestValidator : AbstractValidator<UpdateConfigurationPolicyRequest>
{
    public UpdateConfigurationPolicyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(ConfigurationPolicy.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(ConfigurationPolicy.DescriptionMaxLength);
    }
}

public sealed class SaveAssignmentRequestValidator : AbstractValidator<SaveAssignmentRequest>
{
    public SaveAssignmentRequestValidator()
    {
        RuleFor(x => x.TargetType).IsInEnum();
        RuleFor(x => x.TargetId).NotNull().When(x => x.TargetType != TargetType.AllDevices)
            .WithMessage("targetId is required unless the target is AllDevices.");
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.RemovalBehavior).IsInEnum();
        RuleFor(x => x.Priority).InclusiveBetween(ConfigurationAssignment.MinPriority, ConfigurationAssignment.MaxPriority);
    }
}

public sealed class UpdateAssignmentRequestValidator : AbstractValidator<UpdateAssignmentRequest>
{
    public UpdateAssignmentRequestValidator()
    {
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.RemovalBehavior).IsInEnum();
        RuleFor(x => x.Priority).InclusiveBetween(ConfigurationAssignment.MinPriority, ConfigurationAssignment.MaxPriority);
    }
}
