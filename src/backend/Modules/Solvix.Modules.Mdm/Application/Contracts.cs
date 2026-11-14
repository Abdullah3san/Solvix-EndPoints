namespace Solvix.Modules.Mdm.Application;

using Solvix.Modules.Mdm.Domain;

public sealed record PolicyDto(
    Guid Id,
    string Name,
    string? Description,
    MobilePlatform Platform,
    ManagementMode Mode,
    DeviceOwnership Ownership,
    int Version,
    int PublishedVersion,
    DateTimeOffset? PublishedAtUtc,
    MobilePolicySettings Settings,
    IReadOnlyList<PolicyAppDto> Apps,
    int DeviceCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreatePolicyRequest(
    string Name,
    string? Description,
    MobilePlatform Platform,
    ManagementMode Mode,
    DeviceOwnership Ownership,
    MobilePolicySettings? Settings);

public sealed record UpdatePolicyRequest(string Name, string? Description, MobilePolicySettings? Settings);

public sealed record PublishPolicyResultDto(
    PolicyDto Policy,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped,
    int DevicesNotified);

public sealed record PolicyAppDto(Guid AppId, string Name, string Identifier, AppInstallType InstallType, string? ManagedConfigurationJson);

public sealed record PolicyAppAssignment(Guid AppId, AppInstallType InstallType, string? ManagedConfigurationJson);

public sealed record SetPolicyAppsRequest(IReadOnlyList<PolicyAppAssignment>? Apps);

public sealed record AppDto(Guid Id, MobilePlatform Platform, string Name, string Identifier, AppStoreSource Source, long? StoreId, DateTimeOffset CreatedAtUtc)
{
    public static AppDto From(MobileApp a) => new(a.Id, a.Platform, a.Name, a.Identifier, a.Source, a.StoreId, a.CreatedAtUtc);
}

public sealed record CreateAppRequest(MobilePlatform Platform, string Name, string Identifier, long? StoreId);

public sealed record UpdateAppRequest(string Name, long? StoreId);

public sealed record EnterpriseDto(
    MobilePlatform Platform,
    bool IsBound,
    string? EnterpriseName,
    string? DisplayName,
    DateTimeOffset? BoundAtUtc,
    DateTimeOffset? LastSyncAtUtc,
    bool ProviderConfigured);

public sealed record StartSignupRequest(string CallbackUrl);

public sealed record SignupUrlDto(string Url);

public sealed record CompleteSignupRequest(string EnterpriseToken, string? DisplayName);

public sealed record CreateEnrollmentRequest(
    string Name,
    MobilePlatform Platform,
    ManagementMode Mode,
    DeviceOwnership Ownership,
    Guid PolicyId,
    int ValidForHours = 24,
    int MaxUses = 1);

public sealed record EnrollmentDto(
    Guid Id,
    string Name,
    MobilePlatform Platform,
    ManagementMode Mode,
    DeviceOwnership Ownership,
    EnrollmentMethod Method,
    Guid PolicyId,
    DateTimeOffset ExpiresAtUtc,
    int MaxUses,
    int UseCount,
    DateTimeOffset? RevokedAtUtc,
    bool IsUsable,
    DateTimeOffset CreatedAtUtc)
{
    public static EnrollmentDto From(EnrollmentToken t, DateTimeOffset now) => new(
        t.Id, t.Name, t.Platform, t.Mode, t.Ownership, t.Method, t.PolicyId, t.ExpiresAtUtc, t.MaxUses, t.UseCount, t.RevokedAtUtc, t.IsUsable(now), t.CreatedAtUtc);
}

/// <summary>Returned once at creation: the secret-bearing artefacts are never retrievable again.</summary>
public sealed record EnrollmentCreatedDto(
    EnrollmentDto Enrollment,
    string? QrPayload,
    string? TokenValue,
    string? EnrollmentUrl,
    IReadOnlyList<string> Instructions);

public sealed record DeviceDto(
    Guid Id,
    MobilePlatform Platform,
    ManagementMode Mode,
    DeviceOwnership Ownership,
    DeviceState State,
    string? DisplayName,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? OsVersion,
    string? SecurityPatchLevel,
    bool? IsEncrypted,
    bool? HasPasscode,
    bool? IsSupervised,
    IntegrityState Integrity,
    Guid? PolicyId,
    int? AppliedPolicyVersion,
    ComplianceState Compliance,
    DateTimeOffset? ComplianceEvaluatedAtUtc,
    DateTimeOffset? EnrolledAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? RetiredAtUtc,
    DateTimeOffset CreatedAtUtc)
{
    public static DeviceDto From(MobileDevice d) => new(
        d.Id, d.Platform, d.Mode, d.Ownership, d.State, d.DisplayName, d.Manufacturer, d.Model, d.SerialNumber, d.OsVersion,
        d.SecurityPatchLevel, d.IsEncrypted, d.HasPasscode, d.IsSupervised, d.Integrity, d.PolicyId, d.AppliedPolicyVersion,
        d.Compliance, d.ComplianceEvaluatedAtUtc, d.EnrolledAtUtc, d.LastSeenAtUtc, d.RetiredAtUtc, d.CreatedAtUtc);
}

public sealed record DeviceListQuery(
    MobilePlatform? Platform,
    DeviceState? State,
    ComplianceState? Compliance,
    DeviceOwnership? Ownership,
    string? Search,
    int Page = 1,
    int PageSize = 25);

public sealed record DeviceEventDto(Guid Id, DeviceEventType Type, string Summary, Guid? ActorUserId, Guid? CommandId, DateTimeOffset CreatedAtUtc)
{
    public static DeviceEventDto From(DeviceEvent e) => new(e.Id, e.Type, e.Summary, e.ActorUserId, e.CommandId, e.CreatedAtUtc);
}

public sealed record CapabilitiesDto(
    MobilePlatform Platform,
    ManagementMode Mode,
    DeviceOwnership Ownership,
    IReadOnlyList<DeviceCommandType> Commands,
    IReadOnlyList<DeviceCommandType> CommandsRequiringApproval,
    bool CanRemoveCorporateDataOnly,
    bool CanFactoryReset,
    bool CanReadDeviceIdentifiers,
    bool CanEnforceDeviceWidePolicy,
    IReadOnlyList<string> Limitations)
{
    public static CapabilitiesDto From(MdmCapabilitySet set) => new(
        set.Scenario.Platform,
        set.Scenario.Mode,
        set.Scenario.Ownership,
        set.Commands.OrderBy(c => c).ToList(),
        set.Commands.Where(MdmCapabilities.RequiresApproval).OrderBy(c => c).ToList(),
        set.CanRemoveCorporateDataOnly,
        set.CanFactoryReset,
        set.CanReadDeviceIdentifiers,
        set.CanEnforceDeviceWidePolicy,
        set.Limitations);
}

public sealed record DeviceDetailDto(
    DeviceDto Device,
    IReadOnlyList<InstalledApp> Apps,
    IReadOnlyList<ComplianceViolation> Violations,
    IReadOnlyList<DeviceEventDto> Events,
    IReadOnlyList<CommandDto> Commands,
    CapabilitiesDto Capabilities);

public sealed record AssignPolicyRequest(Guid? PolicyId);

public sealed record CommandDto(
    Guid Id,
    Guid DeviceId,
    DeviceCommandType Type,
    CommandState State,
    bool RequiresApproval,
    string? Reason,
    Guid RequestedByUserId,
    Guid? DecidedByUserId,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionNote,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? Error,
    DateTimeOffset CreatedAtUtc)
{
    public static CommandDto From(DeviceCommand c) => new(
        c.Id, c.DeviceId, c.Type, c.State, c.RequiresApproval, c.Reason, c.RequestedByUserId, c.DecidedByUserId, c.DecidedAtUtc,
        c.DecisionNote, c.DispatchedAtUtc, c.CompletedAtUtc, c.ExpiresAtUtc, c.Error, c.CreatedAtUtc);
}

public sealed record CreateCommandRequest(DeviceCommandType Type, string? Reason, Guid? AppId, string? Message);

public sealed record CommandDecisionRequest(string? Note);

public sealed record CommandListQuery(Guid? DeviceId, CommandState? State, int Page = 1, int PageSize = 25);

public sealed record ComplianceSummaryDto(
    int Total,
    int Compliant,
    int NonCompliant,
    int Unknown,
    IReadOnlyList<ComplianceBucketDto> ByPlatform,
    IReadOnlyList<ComplianceBucketDto> ByOwnership,
    IReadOnlyList<ViolationCountDto> TopViolations,
    int PendingApprovals,
    DateTimeOffset GeneratedAtUtc);

public sealed record ComplianceBucketDto(string Key, int Total, int Compliant, int NonCompliant, int Unknown);

public sealed record ViolationCountDto(string Rule, int Devices);
