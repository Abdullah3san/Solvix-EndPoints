namespace Solvix.Endpoint.Contracts.Agent;

using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Endpoint.Contracts.Patching;

/// <summary>Authentication scheme name used by agents: <c>Authorization: Device &lt;credential&gt;</c>.</summary>
public static class AgentAuth
{
    public const string Scheme = "Device";

    public const string EnrollmentTokenPrefix = "sxe_";

    public const string DeviceCredentialPrefix = "sxd_";
}

// ------------------------------------------------------------------ enrolment

public sealed record EnrollDeviceRequest(
    string EnrollmentToken,
    string Hostname,
    string? HardwareId,
    string? DnsDomain,
    string OsName,
    string OsVersion,
    string OsBuild,
    string AgentVersion);

/// <summary><see cref="DeviceCredential"/> is shown exactly once; the server stores only its hash.</summary>
public sealed record EnrollDeviceResponse(Guid DeviceId, Guid TenantId, string DeviceCredential, int PollIntervalSeconds);

// ------------------------------------------------------------------ heartbeat

public sealed record AgentHeartbeatRequest(
    string Hostname,
    string? DnsDomain,
    string OsName,
    string OsVersion,
    string OsBuild,
    string AgentVersion,
    DateTimeOffset LastBootUtc);

/// <summary>Work waiting for the device. The agent polls this on every heartbeat.</summary>
public sealed record AgentWorkResponse(
    IReadOnlyList<PatchJobDto> PatchJobs,
    IReadOnlyList<RestartRequestDto> Restarts,
    string ConfigurationRevision,
    bool InventoryRequested,
    int PollIntervalSeconds);

// --------------------------------------------------------------- patch inventory

public sealed record PatchInventoryReport(
    DateTimeOffset ScanStartedUtc,
    DateTimeOffset ScanCompletedUtc,
    OperationResultCode ScanResult,
    int? ScanHResult,
    string? ScanError,
    UpdateServiceStatus Service,
    RebootStatus Reboot,
    IReadOnlyList<UpdateInfo> Updates,
    IReadOnlyList<UpdateHistoryEntry> History);

/// <summary>One update known to the Windows Update Agent on the device (installed or not).</summary>
public sealed record UpdateInfo(
    string UpdateId,
    int RevisionNumber,
    string Title,
    IReadOnlyList<string> KbArticleIds,
    UpdateClassification Classification,
    MsrcSeverity Severity,
    DateTimeOffset? ReleaseDateUtc,
    long? MaxDownloadSizeBytes,
    RebootBehavior RebootBehavior,
    bool IsInstalled,
    bool IsDownloaded,
    bool IsHidden,
    bool IsMandatory,
    bool IsRebootRequired,
    string? SupportUrl,
    IReadOnlyList<string> CveIds);

/// <summary>An entry of <c>IUpdateSearcher.QueryHistory</c>.</summary>
public sealed record UpdateHistoryEntry(
    string UpdateId,
    int RevisionNumber,
    string Title,
    UpdateOperation Operation,
    OperationResultCode ResultCode,
    int HResult,
    DateTimeOffset DateUtc);

public sealed record UpdateServiceStatus(
    string WindowsUpdateServiceState,
    string? AutomaticUpdatesNotificationLevel,
    string? UpdateSource,
    DateTimeOffset? LastSuccessfulSearchUtc,
    DateTimeOffset? LastSuccessfulInstallUtc);

public sealed record RebootStatus(bool RebootRequired, IReadOnlyList<string> Reasons, DateTimeOffset LastBootUtc);

// ------------------------------------------------------------------ patch jobs

public sealed record PatchJobDto(
    Guid JobId,
    Guid DeploymentId,
    string DeploymentName,
    int Attempt,
    IReadOnlyList<PatchJobUpdate> Updates,
    DateTimeOffset LeaseExpiresUtc,
    DateTimeOffset? InstallWindowEndsUtc,
    RestartBehavior RestartBehavior,
    DateTimeOffset? DeadlineUtc);

public sealed record PatchJobUpdate(string UpdateId, int RevisionNumber, string Title);

public sealed record PatchJobProgress(int Attempt, PatchJobStage Stage, int PercentComplete, string? Message);

public sealed record PatchJobResult(
    int Attempt,
    PatchJobOutcome Outcome,
    bool RebootRequired,
    IReadOnlyList<PatchUpdateResult> Updates,
    string? Error,
    int? HResult);

public sealed record PatchUpdateResult(string UpdateId, OperationResultCode ResultCode, int HResult, bool RebootRequired);

// ------------------------------------------------------------------ restarts

public sealed record RestartRequestDto(
    Guid RestartId,
    string Reason,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset? WindowEndsUtc,
    int GracePeriodSeconds,
    string UserMessage);

public sealed record RestartAck(RestartAckStatus Status, string? Message);

// ------------------------------------------------------------- configuration

/// <summary>
/// The desired configuration of one device. <see cref="Revision"/> changes whenever
/// anything in the document changes; the agent sends it back in <c>If-None-Match</c>.
/// </summary>
public sealed record ConfigurationDocument(
    string Revision,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<DesiredSetting> Settings,
    IReadOnlyList<string> RevertKeys,
    IReadOnlyList<string> ConflictedKeys);

/// <summary>
/// A setting the device must evaluate. <see cref="AuthorizedRisk"/> is the highest risk
/// the approvers of the policy version accepted; the agent refuses anything above it.
/// <see cref="ApplyAllowed"/> is false outside the assignment's maintenance window.
/// </summary>
public sealed record DesiredSetting(
    string Key,
    ConfigurationSetting Setting,
    SettingRisk AuthorizedRisk,
    EnforcementMode Mode,
    bool ApplyAllowed,
    Guid PolicyId,
    Guid PolicyVersionId,
    int PolicyVersion,
    Guid AssignmentId,
    string Hash);

public sealed record ConfigurationReport(string Revision, IReadOnlyList<SettingResult> Results);

public sealed record SettingResult(
    string Key,
    Guid? PolicyVersionId,
    SettingComplianceStatus Status,
    string? ExpectedValue,
    string? ActualValue,
    string? Message,
    bool RollbackAvailable,
    DateTimeOffset EvaluatedAtUtc);
