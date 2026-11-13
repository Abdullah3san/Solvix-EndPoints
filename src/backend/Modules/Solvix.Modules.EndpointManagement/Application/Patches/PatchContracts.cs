namespace Solvix.Modules.EndpointManagement.Application.Patches;

using FluentValidation;
using Solvix.Endpoint.Contracts.Patching;
using Solvix.Modules.EndpointManagement.Domain.Patches;

public sealed record PatchDto(
    Guid Id,
    string UpdateId,
    int RevisionNumber,
    string Title,
    IReadOnlyList<string> KbArticleIds,
    IReadOnlyList<string> CveIds,
    UpdateClassification Classification,
    MsrcSeverity Severity,
    DateTimeOffset? ReleaseDateUtc,
    long? MaxDownloadSizeBytes,
    RebootBehavior RebootBehavior,
    string? SupportUrl,
    PatchApprovalState ApprovalState,
    DateTimeOffset? ApprovalDecidedAtUtc,
    Guid? ApprovalDecidedByUserId,
    Guid? AutoApprovalRuleId,
    string? ApprovalNote,
    DateTimeOffset FirstSeenAtUtc,
    int DevicesMissing,
    int DevicesFailed,
    int DevicesPendingRestart,
    int DevicesInstalled);

public sealed record PatchListQuery(
    string? Search,
    UpdateClassification? Classification,
    MsrcSeverity? Severity,
    PatchApprovalState? ApprovalState,
    bool OnlyNeeded = false,
    int Page = 1,
    int PageSize = 50);

public sealed record PatchDeviceStateDto(
    Guid DeviceId,
    string Hostname,
    DevicePatchStatus Status,
    bool IsDownloaded,
    DateTimeOffset LastDetectedAtUtc,
    DateTimeOffset? InstalledAtUtc,
    int FailureCount,
    int? LastFailureHResult,
    DateTimeOffset? LastFailureAtUtc);

public sealed record PatchDetailDto(PatchDto Patch, IReadOnlyList<PatchDeviceStateDto> Devices);

public sealed record PatchDecisionRequest(IReadOnlyList<Guid> PatchIds, string? Note);

public sealed record AutoApprovalRuleDto(
    Guid Id,
    string Name,
    IReadOnlyList<UpdateClassification> Classifications,
    IReadOnlyList<MsrcSeverity> Severities,
    bool IsEnabled,
    DateTimeOffset CreatedAtUtc);

public sealed record SaveAutoApprovalRuleRequest(
    string Name,
    IReadOnlyList<UpdateClassification> Classifications,
    IReadOnlyList<MsrcSeverity>? Severities,
    bool IsEnabled = true,
    bool ApplyToExisting = false);

public enum DeviceComplianceStatus
{
    Compliant = 1,
    PendingRestart = 2,
    NonCompliant = 3,

    /// <summary>Never scanned, or the last successful scan is older than the staleness limit.</summary>
    Unknown = 4,
}

public sealed record DevicePatchComplianceDto(
    Guid DeviceId,
    string Hostname,
    Guid? BranchId,
    Guid? DepartmentId,
    DeviceComplianceStatus Status,
    int MissingApproved,
    int MissingCritical,
    int MissingSecurity,
    int MissingTotal,
    int Failed,
    int PendingRestart,
    bool RebootRequired,
    DateTimeOffset? RebootRequiredSinceUtc,
    DateTimeOffset? LastSuccessfulScanUtc,
    DateTimeOffset LastSeenAtUtc);

public sealed record ComplianceBreakdownDto(Guid? Id, string Name, int Devices, int Compliant, double CompliancePercent);

public sealed record MissingPatchSummaryDto(Guid PatchId, string Title, IReadOnlyList<string> KbArticleIds, MsrcSeverity Severity, UpdateClassification Classification, PatchApprovalState ApprovalState, int DevicesMissing);

public sealed record PatchComplianceReportDto(
    DateTimeOffset GeneratedAtUtc,
    int StaleAfterDays,
    int Devices,
    int Compliant,
    int PendingRestart,
    int NonCompliant,
    int Unknown,
    double CompliancePercent,
    IReadOnlyList<ComplianceBreakdownDto> ByBranch,
    IReadOnlyList<ComplianceBreakdownDto> ByDepartment,
    IReadOnlyList<ComplianceBreakdownDto> ByDeviceGroup,
    IReadOnlyList<MissingPatchSummaryDto> TopMissing,
    IReadOnlyList<DevicePatchComplianceDto> DeviceRows);

public sealed record ComplianceQuery(Guid? BranchId, Guid? DepartmentId, Guid? DeviceGroupId, int StaleAfterDays = 7);

public sealed record PatchFailureDto(
    Guid DeviceId,
    string Hostname,
    Guid PatchId,
    string Title,
    IReadOnlyList<string> KbArticleIds,
    int FailureCount,
    int? LastFailureHResult,
    string? LastFailureHex,
    DateTimeOffset? LastFailureAtUtc,
    bool RepeatedFailure);

public sealed record DeviceUpdateStatusDto(
    Guid DeviceId,
    string Hostname,
    DateTimeOffset? LastScanCompletedUtc,
    OperationResultCode? LastScanResult,
    string? LastScanError,
    DateTimeOffset? LastSuccessfulScanUtc,
    string? ServiceState,
    string? AutomaticUpdatesNotificationLevel,
    string? UpdateSource,
    DateTimeOffset? LastSuccessfulInstallUtc,
    bool RebootRequired,
    IReadOnlyList<string> RebootReasons,
    DateTimeOffset? RebootRequiredSinceUtc,
    DateTimeOffset? LastBootUtc,
    bool InventoryRequested,
    IReadOnlyList<DevicePatchRowDto> Patches);

public sealed record DevicePatchRowDto(
    Guid PatchId,
    string Title,
    IReadOnlyList<string> KbArticleIds,
    UpdateClassification Classification,
    MsrcSeverity Severity,
    PatchApprovalState ApprovalState,
    DevicePatchStatus Status,
    DateTimeOffset? InstalledAtUtc,
    int FailureCount,
    int? LastFailureHResult);

public sealed class PatchDecisionRequestValidator : AbstractValidator<PatchDecisionRequest>
{
    public PatchDecisionRequestValidator()
    {
        RuleFor(x => x.PatchIds).NotNull().Must(ids => ids.Count is > 0 and <= 500).WithMessage("Provide between 1 and 500 patch ids.");
        RuleFor(x => x.Note).MaximumLength(Patch.NoteMaxLength);
    }
}

public sealed class SaveAutoApprovalRuleRequestValidator : AbstractValidator<SaveAutoApprovalRuleRequest>
{
    public SaveAutoApprovalRuleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(PatchAutoApprovalRule.NameMaxLength);
        RuleFor(x => x.Classifications).NotNull().Must(c => c.Count > 0).WithMessage("Select at least one classification.");
        RuleForEach(x => x.Classifications).IsInEnum();
        RuleForEach(x => x.Severities).IsInEnum();
    }
}
