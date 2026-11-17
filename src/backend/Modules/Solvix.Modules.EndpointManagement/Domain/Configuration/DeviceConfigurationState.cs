namespace Solvix.Modules.EndpointManagement.Domain.Configuration;

using Solvix.Endpoint.Contracts.Configuration;
using Solvix.SharedKernel.Entities;

/// <summary>
/// The server's view of one setting key on one device: what is desired (if
/// anything), what the agent last reported, and what should happen when the
/// setting stops applying. Drives compliance, drift and rollback.
/// </summary>
public sealed class DeviceConfigurationState : TenantEntity
{
    public const int KeyMaxLength = 600;
    public const int ValueMaxLength = 4000;

    private DeviceConfigurationState()
    {
    }

    public DeviceConfigurationState(Guid tenantId, Guid deviceId, string settingKey)
        : base(tenantId)
    {
        DeviceId = deviceId;
        SettingKey = settingKey.Length <= KeyMaxLength ? settingKey : settingKey[..KeyMaxLength];
    }

    public Guid DeviceId { get; private set; }

    public string SettingKey { get; private set; } = string.Empty;

    /// <summary>True while the setting is part of the device's effective configuration.</summary>
    public bool IsDesired { get; private set; }

    /// <summary>True while two policies set this key differently with equal precedence (nothing is applied).</summary>
    public bool IsConflicted { get; private set; }

    public Guid? PolicyId { get; private set; }

    public Guid? PolicyVersionId { get; private set; }

    public Guid? AssignmentId { get; private set; }

    public EnforcementMode? Mode { get; private set; }

    public string? ExpectedHash { get; private set; }

    public RemovalBehavior RemovalBehavior { get; private set; } = RemovalBehavior.Leave;

    /// <summary>Null until the agent first reports on the key.</summary>
    public SettingComplianceStatus? Status { get; private set; }

    public string? ExpectedValue { get; private set; }

    public string? ActualValue { get; private set; }

    public string? Message { get; private set; }

    public bool RollbackAvailable { get; private set; }

    /// <summary>Whether the agent ever changed this key (only such keys are reverted).</summary>
    public bool WasChangedBySolvix { get; private set; }

    public DateTimeOffset? LastEvaluatedAtUtc { get; private set; }

    public DateTimeOffset? StatusChangedAtUtc { get; private set; }

    public DateTimeOffset? DriftDetectedAtUtc { get; private set; }

    /// <summary>
    /// No longer desired (assignment removed, policy changed or device left the target),
    /// the agent changed it, its last assignment asked for a revert, and it has not been reverted yet.
    /// </summary>
    public bool NeedsRevert =>
        !IsDesired && !IsConflicted && RemovalBehavior == RemovalBehavior.Revert && WasChangedBySolvix
        && Status is not (SettingComplianceStatus.Reverted or SettingComplianceStatus.RevertFailed or SettingComplianceStatus.RevertUnsupported);

    public void MarkDesired(Guid policyId, Guid policyVersionId, Guid assignmentId, EnforcementMode mode, RemovalBehavior removalBehavior, string expectedHash)
    {
        IsDesired = true;
        IsConflicted = false;
        PolicyId = policyId;
        PolicyVersionId = policyVersionId;
        AssignmentId = assignmentId;
        Mode = mode;
        RemovalBehavior = removalBehavior;
        if (ExpectedHash != expectedHash)
        {
            ExpectedHash = expectedHash;

            // A new desired value invalidates any earlier "reverted" outcome.
            if (Status is SettingComplianceStatus.Reverted or SettingComplianceStatus.RevertFailed or SettingComplianceStatus.RevertUnsupported)
            {
                Status = null;
            }
        }
    }

    public void MarkConflicted()
    {
        IsDesired = false;
        IsConflicted = true;
    }

    public void MarkNotDesired()
    {
        IsDesired = false;
        IsConflicted = false;
    }

    /// <summary>Applies an agent report. Returns true when the status changed.</summary>
    public bool Report(SettingComplianceStatus status, string? expected, string? actual, string? message, bool rollbackAvailable, DateTimeOffset evaluatedAt)
    {
        var changed = Status != status;
        if (status is SettingComplianceStatus.Applied or SettingComplianceStatus.Remediated)
        {
            WasChangedBySolvix = true;
        }

        if (status == SettingComplianceStatus.Drifted && Status != SettingComplianceStatus.Drifted)
        {
            DriftDetectedAtUtc = evaluatedAt;
        }
        else if (status is SettingComplianceStatus.Compliant or SettingComplianceStatus.Applied)
        {
            DriftDetectedAtUtc = null;
        }

        if (status is SettingComplianceStatus.Reverted)
        {
            WasChangedBySolvix = false;
        }

        Status = status;
        ExpectedValue = Clip(expected);
        ActualValue = Clip(actual);
        Message = Clip(message);
        RollbackAvailable = rollbackAvailable;
        LastEvaluatedAtUtc = evaluatedAt;
        if (changed)
        {
            StatusChangedAtUtc = evaluatedAt;
        }

        return changed;
    }

    private static string? Clip(string? value) =>
        value is null ? null : value.Length <= ValueMaxLength ? value : value[..ValueMaxLength];
}

/// <summary>Append-only history of detected drift and its remediation.</summary>
public sealed class ConfigurationDriftEvent : TenantEntity
{
    private ConfigurationDriftEvent()
    {
    }

    public ConfigurationDriftEvent(
        Guid tenantId,
        Guid deviceId,
        string settingKey,
        Guid? policyVersionId,
        SettingComplianceStatus status,
        EnforcementMode? mode,
        string? expectedValue,
        string? actualValue,
        DateTimeOffset detectedAtUtc)
        : base(tenantId)
    {
        DeviceId = deviceId;
        SettingKey = settingKey.Length <= DeviceConfigurationState.KeyMaxLength ? settingKey : settingKey[..DeviceConfigurationState.KeyMaxLength];
        PolicyVersionId = policyVersionId;
        Status = status;
        Mode = mode;
        ExpectedValue = Clip(expectedValue);
        ActualValue = Clip(actualValue);
        DetectedAtUtc = detectedAtUtc;
    }

    public Guid DeviceId { get; private set; }

    public string SettingKey { get; private set; } = string.Empty;

    public Guid? PolicyVersionId { get; private set; }

    /// <summary>Drifted (reported only) or Remediated (corrected by the agent).</summary>
    public SettingComplianceStatus Status { get; private set; }

    public EnforcementMode? Mode { get; private set; }

    public string? ExpectedValue { get; private set; }

    public string? ActualValue { get; private set; }

    public DateTimeOffset DetectedAtUtc { get; private set; }

    private static string? Clip(string? value) =>
        value is null ? null : value.Length <= DeviceConfigurationState.ValueMaxLength ? value : value[..DeviceConfigurationState.ValueMaxLength];
}
