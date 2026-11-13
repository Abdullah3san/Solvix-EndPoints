namespace Solvix.Modules.Mdm.Domain;

using System.Text.Json;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A mobile device under management. Ownership is fixed at enrollment and
/// decides what data the organisation may hold: on personally owned devices
/// hardware identifiers and the personal application inventory are never stored.
/// </summary>
public sealed class MobileDevice : TenantEntity
{
    public const int IdentifierMaxLength = 256;
    public const int TextMaxLength = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private MobileDevice()
    {
    }

    private MobileDevice(Guid tenantId, ManagementScenario scenario, string providerDeviceId)
        : base(tenantId)
    {
        Platform = scenario.Platform;
        Mode = scenario.Mode;
        Ownership = scenario.Ownership;
        ProviderDeviceId = providerDeviceId;
    }

    public MobilePlatform Platform { get; private set; }

    public ManagementMode Mode { get; private set; }

    public DeviceOwnership Ownership { get; private set; }

    public DeviceState State { get; private set; } = DeviceState.Pending;

    /// <summary>Android: Android Management API device resource name. iOS: opaque routing handle in the MDM URLs.</summary>
    public string ProviderDeviceId { get; private set; } = string.Empty;

    /// <summary>iOS: UDID (device enrollment) or EnrollmentID (User Enrollment).</summary>
    public string? ManagementIdentifier { get; private set; }

    public Guid? EnrollmentTokenId { get; private set; }

    public Guid? PolicyId { get; private set; }

    public int? AppliedPolicyVersion { get; private set; }

    public string? DisplayName { get; private set; }

    public string? Manufacturer { get; private set; }

    public string? Model { get; private set; }

    public string? SerialNumber { get; private set; }

    public string? OsVersion { get; private set; }

    public string? SecurityPatchLevel { get; private set; }

    public bool? IsEncrypted { get; private set; }

    public bool? HasPasscode { get; private set; }

    public bool? IsSupervised { get; private set; }

    public IntegrityState Integrity { get; private set; }

    public string InstalledAppsJson { get; private set; } = "[]";

    /// <summary>When an application inventory was last received. Null = never reported.</summary>
    public DateTimeOffset? AppInventoryAtUtc { get; private set; }

    public DateTimeOffset? EnrolledAtUtc { get; private set; }

    public DateTimeOffset? LastSeenAtUtc { get; private set; }

    public DateTimeOffset? RetiredAtUtc { get; private set; }

    public ComplianceState Compliance { get; private set; }

    public DateTimeOffset? ComplianceEvaluatedAtUtc { get; private set; }

    public string ComplianceViolationsJson { get; private set; } = "[]";

    // ---- Apple MDM transport state (never returned by the API) ----

    public string? PushToken { get; private set; }

    public string? PushMagic { get; private set; }

    public string? PushTopic { get; private set; }

    /// <summary>Encrypted at rest; required by ClearPasscode.</summary>
    public string? ProtectedUnlockToken { get; private set; }

    /// <summary>SHA-256 thumbprint of the identity certificate the device signs its messages with.</summary>
    public string? IdentityThumbprint { get; private set; }

    public ManagementScenario Scenario => new(Platform, Mode, Ownership);

    public bool IsActive => State is DeviceState.Enrolled or DeviceState.RemovalPending;

    public IReadOnlyList<InstalledApp> InstalledApps =>
        JsonSerializer.Deserialize<List<InstalledApp>>(InstalledAppsJson, Json) ?? [];

    public IReadOnlyList<ComplianceViolation> ComplianceViolations =>
        JsonSerializer.Deserialize<List<ComplianceViolation>>(ComplianceViolationsJson, Json) ?? [];

    /// <summary>Creates a device that has been issued an enrollment but has not checked in yet.</summary>
    public static MobileDevice CreatePending(Guid tenantId, ManagementScenario scenario, string providerDeviceId, Guid? enrollmentTokenId, Guid? policyId)
    {
        scenario.EnsureValid();
        return new MobileDevice(tenantId, scenario, Guard.NotNullOrWhiteSpace(providerDeviceId, "providerDeviceId", IdentifierMaxLength))
        {
            EnrollmentTokenId = enrollmentTokenId,
            PolicyId = policyId,
        };
    }

    public void CompleteEnrollment(DateTimeOffset now, string? managementIdentifier = null)
    {
        if (State is DeviceState.Retired or DeviceState.Wiped)
        {
            throw new ConflictException("A retired device cannot re-enroll under the same record. Issue a new enrollment.");
        }

        if (managementIdentifier is not null)
        {
            ManagementIdentifier = Guard.Optional(managementIdentifier, "managementIdentifier", IdentifierMaxLength);
        }

        if (State == DeviceState.Pending)
        {
            State = DeviceState.Enrolled;
            EnrolledAtUtc = now;
        }

        LastSeenAtUtc = now;
    }

    /// <summary>Records the Apple UDID / EnrollmentID reported at Authenticate.</summary>
    public void CompleteEnrollmentIdentifier(string? identifier) =>
        ManagementIdentifier = Guard.Optional(identifier, "managementIdentifier", IdentifierMaxLength) ?? ManagementIdentifier;

    public void AssignPolicy(MobilePolicy? policy)
    {
        if (policy is null)
        {
            PolicyId = null;
            AppliedPolicyVersion = null;
            return;
        }

        if (policy.Scenario != Scenario)
        {
            throw new ConflictException("The policy targets a different platform, management mode or ownership than this device.");
        }

        if (PolicyId != policy.Id)
        {
            AppliedPolicyVersion = null;
        }

        PolicyId = policy.Id;
    }

    public void MarkPolicyApplied(int version) => AppliedPolicyVersion = version;

    /// <summary>
    /// Merges an inventory report. On personally owned devices hardware identifiers
    /// and unmanaged apps are discarded here, so even a provider that over-reports
    /// cannot place personal data into the tenant's database.
    /// </summary>
    public void ApplyInventory(DeviceInventory inventory, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var personal = Ownership == DeviceOwnership.PersonallyOwned;

        DisplayName = Guard.Optional(inventory.DisplayName, "displayName", TextMaxLength) ?? DisplayName;
        Manufacturer = Guard.Optional(inventory.Manufacturer, "manufacturer", TextMaxLength) ?? Manufacturer;
        Model = Guard.Optional(inventory.Model, "model", TextMaxLength) ?? Model;
        OsVersion = Guard.Optional(inventory.OsVersion, "osVersion", 64) ?? OsVersion;
        SecurityPatchLevel = Guard.Optional(inventory.SecurityPatchLevel, "securityPatchLevel", 64) ?? SecurityPatchLevel;
        IsEncrypted = inventory.IsEncrypted ?? IsEncrypted;
        HasPasscode = inventory.HasPasscode ?? HasPasscode;
        IsSupervised = inventory.IsSupervised ?? IsSupervised;
        if (inventory.Integrity != IntegrityState.Unknown)
        {
            Integrity = inventory.Integrity;
        }

        if (!personal)
        {
            SerialNumber = Guard.Optional(inventory.SerialNumber, "serialNumber", TextMaxLength) ?? SerialNumber;
        }

        if (inventory.Apps is not null)
        {
            var apps = personal ? inventory.Apps.Where(a => a.IsManaged) : inventory.Apps;
            InstalledAppsJson = JsonSerializer.Serialize(apps.Take(2000).ToList(), Json);
            AppInventoryAtUtc = now;
        }

        LastSeenAtUtc = inventory.LastSeenAtUtc ?? now;
    }

    public void SetCompliance(ComplianceState state, IReadOnlyList<ComplianceViolation> violations, DateTimeOffset now)
    {
        Compliance = state;
        ComplianceEvaluatedAtUtc = now;
        ComplianceViolationsJson = JsonSerializer.Serialize(violations, Json);
    }

    public void RecordContact(DateTimeOffset now) => LastSeenAtUtc = now;

    public void BindAppleTransport(string? pushToken, string? pushMagic, string? topic, string? protectedUnlockToken)
    {
        PushToken = pushToken ?? PushToken;
        PushMagic = pushMagic ?? PushMagic;
        PushTopic = topic ?? PushTopic;
        ProtectedUnlockToken = protectedUnlockToken ?? ProtectedUnlockToken;
    }

    public void BindIdentity(string thumbprint)
    {
        if (IdentityThumbprint is not null && !string.Equals(IdentityThumbprint, thumbprint, StringComparison.Ordinal))
        {
            throw new ForbiddenException("The device identity certificate does not match the one it enrolled with.");
        }

        IdentityThumbprint = thumbprint;
    }

    public void BeginRemoval()
    {
        if (State != DeviceState.Enrolled)
        {
            throw new ConflictException($"Only enrolled devices can be removed (current state: {State}).");
        }

        State = DeviceState.RemovalPending;
    }

    public void CancelRemoval()
    {
        if (State == DeviceState.RemovalPending)
        {
            State = DeviceState.Enrolled;
        }
    }

    /// <summary>Management was removed. <paramref name="factoryReset"/> distinguishes a full wipe from selective removal.</summary>
    public void CompleteRemoval(DateTimeOffset now, bool factoryReset)
    {
        State = factoryReset ? DeviceState.Wiped : DeviceState.Retired;
        RetiredAtUtc = now;
        PushToken = null;
        PushMagic = null;
        ProtectedUnlockToken = null;
    }
}
