namespace Solvix.Modules.EndpointManagement.Domain.Patches;

using Solvix.Endpoint.Contracts.Patching;
using Solvix.SharedKernel.Entities;

public enum DevicePatchStatus
{
    /// <summary>Applicable and not installed.</summary>
    Missing = 1,
    Installed = 2,

    /// <summary>Installed; Windows needs a restart to finish.</summary>
    PendingRestart = 3,

    /// <summary>Applicable, not installed, and the last installation attempt failed.</summary>
    Failed = 4,

    /// <summary>Hidden on the device (e.g. by a local administrator).</summary>
    Hidden = 5,

    /// <summary>No longer offered to the device (superseded or not applicable).</summary>
    NotApplicable = 6,
}

/// <summary>The status of one patch on one device, maintained from agent scans.</summary>
public sealed class DevicePatchState : TenantEntity
{
    private DevicePatchState()
    {
    }

    public DevicePatchState(Guid tenantId, Guid deviceId, Guid patchId, DateTimeOffset now)
        : base(tenantId)
    {
        DeviceId = deviceId;
        PatchId = patchId;
        FirstDetectedAtUtc = now;
        LastDetectedAtUtc = now;
        Status = DevicePatchStatus.Missing;
    }

    public Guid DeviceId { get; private set; }

    public Guid PatchId { get; private set; }

    public DevicePatchStatus Status { get; private set; }

    public bool IsDownloaded { get; private set; }

    public DateTimeOffset FirstDetectedAtUtc { get; private set; }

    public DateTimeOffset LastDetectedAtUtc { get; private set; }

    public DateTimeOffset? InstalledAtUtc { get; private set; }

    public DateTimeOffset? LastFailureAtUtc { get; private set; }

    public int? LastFailureHResult { get; private set; }

    public int FailureCount { get; private set; }

    public Patch? Patch { get; private set; }

    public bool IsOutstanding => Status is DevicePatchStatus.Missing or DevicePatchStatus.Failed;

    public void Observe(DevicePatchStatus status, bool isDownloaded, DateTimeOffset now, DateTimeOffset? installedAtUtc = null)
    {
        if (status is DevicePatchStatus.Installed or DevicePatchStatus.PendingRestart && InstalledAtUtc is null)
        {
            InstalledAtUtc = installedAtUtc ?? now;
        }

        // A failure recorded by a previous scan or deployment stays visible until the patch installs.
        if (status == DevicePatchStatus.Missing && Status == DevicePatchStatus.Failed)
        {
            status = DevicePatchStatus.Failed;
        }

        Status = status;
        IsDownloaded = isDownloaded;
        LastDetectedAtUtc = now;
    }

    public void RecordFailure(int hresult, DateTimeOffset at)
    {
        if (LastFailureAtUtc is { } last && at <= last)
        {
            return;
        }

        LastFailureAtUtc = at;
        LastFailureHResult = hresult;
        FailureCount++;
        if (Status is DevicePatchStatus.Missing or DevicePatchStatus.Failed)
        {
            Status = DevicePatchStatus.Failed;
        }
    }

    public void MarkInstalled(bool pendingRestart, DateTimeOffset at)
    {
        Status = pendingRestart ? DevicePatchStatus.PendingRestart : DevicePatchStatus.Installed;
        InstalledAtUtc ??= at;
        LastDetectedAtUtc = at;
    }

    public static DevicePatchStatus FromResult(OperationResultCode code) => code switch
    {
        OperationResultCode.Succeeded or OperationResultCode.SucceededWithErrors => DevicePatchStatus.Installed,
        OperationResultCode.Failed or OperationResultCode.Aborted => DevicePatchStatus.Failed,
        _ => DevicePatchStatus.Missing,
    };
}

/// <summary>Per-device Windows Update health, from the latest agent scan.</summary>
public sealed class DeviceUpdateSummary : TenantEntity
{
    private DeviceUpdateSummary()
    {
    }

    public DeviceUpdateSummary(Guid tenantId, Guid deviceId)
        : base(tenantId)
    {
        DeviceId = deviceId;
        InventoryRequested = true;
    }

    public Guid DeviceId { get; private set; }

    public DateTimeOffset? LastScanStartedUtc { get; private set; }

    public DateTimeOffset? LastScanCompletedUtc { get; private set; }

    public OperationResultCode? LastScanResult { get; private set; }

    public int? LastScanHResult { get; private set; }

    public string? LastScanError { get; private set; }

    /// <summary>Last scan that succeeded; compliance treats older data as stale.</summary>
    public DateTimeOffset? LastSuccessfulScanUtc { get; private set; }

    public string? ServiceState { get; private set; }

    public string? AutomaticUpdatesNotificationLevel { get; private set; }

    public string? UpdateSource { get; private set; }

    public DateTimeOffset? LastSuccessfulInstallUtc { get; private set; }

    public bool RebootRequired { get; private set; }

    public string[] RebootReasons { get; private set; } = [];

    public DateTimeOffset? RebootRequiredSinceUtc { get; private set; }

    public DateTimeOffset? LastBootUtc { get; private set; }

    /// <summary>Asks the agent to rescan on its next heartbeat (after a restart, or on admin request).</summary>
    public bool InventoryRequested { get; private set; }

    public void RecordScan(
        DateTimeOffset started,
        DateTimeOffset completed,
        OperationResultCode result,
        int? hresult,
        string? error,
        string? serviceState,
        string? notificationLevel,
        string? updateSource,
        DateTimeOffset? lastSuccessfulInstallUtc)
    {
        LastScanStartedUtc = started;
        LastScanCompletedUtc = completed;
        LastScanResult = result;
        LastScanHResult = hresult;
        LastScanError = error is { Length: > 1000 } ? error[..1000] : error;
        if (result is OperationResultCode.Succeeded or OperationResultCode.SucceededWithErrors)
        {
            LastSuccessfulScanUtc = completed;
        }

        ServiceState = Clip(serviceState, 64);
        AutomaticUpdatesNotificationLevel = Clip(notificationLevel, 64);
        UpdateSource = Clip(updateSource, 64);
        LastSuccessfulInstallUtc = lastSuccessfulInstallUtc ?? LastSuccessfulInstallUtc;
        InventoryRequested = false;
    }

    public void RecordReboot(bool required, IEnumerable<string> reasons, DateTimeOffset lastBootUtc, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        if (required && !RebootRequired)
        {
            RebootRequiredSinceUtc = now;
        }
        else if (!required)
        {
            RebootRequiredSinceUtc = null;
        }

        RebootRequired = required;
        RebootReasons = reasons.Select(r => Clip(r, 200)!).Distinct().Take(20).ToArray();
        LastBootUtc = lastBootUtc;
    }

    public void RequestInventory() => InventoryRequested = true;

    private static string? Clip(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}

/// <summary>Approves newly discovered patches that match a classification/severity filter.</summary>
public sealed class PatchAutoApprovalRule : TenantEntity
{
    public const int NameMaxLength = 200;

    private PatchAutoApprovalRule()
    {
    }

    public PatchAutoApprovalRule(Guid tenantId, string name, UpdateClassification[] classifications, MsrcSeverity[] severities, Guid createdByUserId)
        : base(tenantId)
    {
        CreatedByUserId = createdByUserId;
        IsEnabled = true;
        Update(name, classifications, severities);
    }

    public string Name { get; private set; } = string.Empty;

    public UpdateClassification[] Classifications { get; private set; } = [];

    /// <summary>Empty means "any severity".</summary>
    public MsrcSeverity[] Severities { get; private set; } = [];

    public bool IsEnabled { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public void Update(string name, UpdateClassification[] classifications, MsrcSeverity[] severities)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        ArgumentNullException.ThrowIfNull(severities);
        Name = SharedKernel.Guards.Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        if (classifications.Length == 0)
        {
            throw new SharedKernel.Exceptions.ValidationException("classifications", "Select at least one classification.");
        }

        Classifications = classifications.Distinct().ToArray();
        Severities = severities.Distinct().ToArray();
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public bool Matches(Patch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return IsEnabled
            && Classifications.Contains(patch.Classification)
            && (Severities.Length == 0 || Severities.Contains(patch.Severity));
    }
}
