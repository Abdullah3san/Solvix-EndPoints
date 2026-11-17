namespace Solvix.Endpoint.Contracts.Agent;

using Solvix.Endpoint.Contracts.Configuration;
using Solvix.Endpoint.Contracts.Patching;

/// <summary>
/// Agent routes of endpoint management (Phase 09). The agent authenticates with its device certificate
/// (mutual TLS, Phase 03) like every other agent request.
/// </summary>
public static class EndpointAgentRoutes
{
    public const string Base = "api/v1/agent/endpoint-management";

    public const string Work = Base + "/work";

    public const string PatchInventory = Base + "/patches/inventory";

    public const string Configuration = Base + "/configuration";

    public const string ConfigurationReport = Base + "/configuration/report";
}

// ------------------------------------------------------------------ work

/// <param name="LastBootUtc">Boot time of the device; a change tells the server a restart happened.</param>
public sealed record AgentWorkRequest(DateTimeOffset LastBootUtc);

/// <summary>
/// What the (unprivileged) agent should do next. Installing updates, enforcing configuration and
/// restarting arrive separately as signed commands executed by the privileged executor.
/// </summary>
public sealed record AgentWorkResponse(
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
