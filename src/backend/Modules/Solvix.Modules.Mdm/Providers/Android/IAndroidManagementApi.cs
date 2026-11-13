namespace Solvix.Modules.Mdm.Providers.Android;

using System.Text.Json.Nodes;

/// <summary>
/// The subset of the Android Management API (https://developers.google.com/android/management)
/// that Solvix uses. Kept narrow so it can be replaced in tests and so every outbound call is explicit.
/// </summary>
public interface IAndroidManagementApi
{
    Task<AndroidSignupUrl> CreateSignupUrlAsync(string projectId, string callbackUrl, CancellationToken cancellationToken);

    Task<AndroidEnterpriseInfo> CreateEnterpriseAsync(string projectId, string signupUrlName, string enterpriseToken, string displayName, CancellationToken cancellationToken);

    Task<AndroidEnrollmentTokenInfo> CreateEnrollmentTokenAsync(string enterpriseName, JsonObject token, CancellationToken cancellationToken);

    Task PatchPolicyAsync(string policyName, JsonObject policy, CancellationToken cancellationToken);

    Task<IReadOnlyList<AndroidDeviceInfo>> ListDevicesAsync(string enterpriseName, CancellationToken cancellationToken);

    Task PatchDeviceAsync(string deviceName, JsonObject patch, string updateMask, CancellationToken cancellationToken);

    /// <summary>Issues a device command and returns the long-running operation name.</summary>
    Task<string> IssueCommandAsync(string deviceName, JsonObject command, CancellationToken cancellationToken);

    /// <summary>Reads a long-running operation returned by <see cref="IssueCommandAsync"/>.</summary>
    Task<AndroidOperationInfo> GetOperationAsync(string operationName, CancellationToken cancellationToken);

    /// <summary>Deletes the device resource. On fully managed devices this factory resets; on work-profile devices it removes the work profile.</summary>
    Task DeleteDeviceAsync(string deviceName, IReadOnlyList<string> wipeDataFlags, string? wipeReasonMessage, CancellationToken cancellationToken);
}

public sealed record AndroidOperationInfo(bool Done, string? Error);

public sealed record AndroidSignupUrl(string Name, string Url);

public sealed record AndroidEnterpriseInfo(string Name, string? EnterpriseDisplayName);

public sealed record AndroidEnrollmentTokenInfo(string Name, string Value, string? QrCode, DateTimeOffset? ExpirationTimestamp);

/// <summary>Device resource, reduced to the fields Solvix reads.</summary>
public sealed record AndroidDeviceInfo
{
    public string Name { get; init; } = string.Empty;

    /// <summary>ACTIVE, DISABLED, DELETED, PROVISIONING, LOST, PREPARING_FOR_MIGRATION, DEACTIVATED_BY_DEVICE_FINANCE.</summary>
    public string? State { get; init; }

    /// <summary>DEVICE_OWNER or PROFILE_OWNER.</summary>
    public string? ManagementMode { get; init; }

    /// <summary>COMPANY_OWNED or PERSONALLY_OWNED.</summary>
    public string? Ownership { get; init; }

    public string? EnrollmentTokenData { get; init; }

    public string? AppliedPolicyName { get; init; }

    public int? AppliedPolicyVersion { get; init; }

    public bool? PolicyCompliant { get; init; }

    public DateTimeOffset? LastStatusReportTime { get; init; }

    public DateTimeOffset? LastPolicySyncTime { get; init; }

    public string? Manufacturer { get; init; }

    public string? Brand { get; init; }

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    public string? AndroidVersion { get; init; }

    public string? SecurityPatchLevel { get; init; }

    public bool? IsEncrypted { get; init; }

    public bool? IsDeviceSecure { get; init; }

    /// <summary>SECURE, AT_RISK, POTENTIALLY_COMPROMISED.</summary>
    public string? DevicePosture { get; init; }

    public IReadOnlyList<AndroidAppReport>? Applications { get; init; }
}

public sealed record AndroidAppReport(string PackageName, string? DisplayName, string? VersionName, string? Source);
