namespace Solvix.Modules.Devices.Domain;

using Solvix.Modules.Organization.Domain;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

public enum DeviceStatus
{
    Active = 1,

    /// <summary>Decommissioned: the credential is revoked and the device is excluded from all targeting.</summary>
    Retired = 2,
}

/// <summary>
/// A Windows endpoint enrolled through the Solvix agent. A device belongs to at
/// most one branch and department (used for policy targeting) and authenticates
/// with a per-device credential of which only the SHA-256 hash is stored.
/// </summary>
public sealed class Device : TenantEntity
{
    public const int HostnameMaxLength = 255;
    public const int ShortTextMaxLength = 128;
    public const int AgentVersionMaxLength = 64;

    private Device()
    {
    }

    private Device(Guid tenantId, string hostname, string? hardwareId, DateTimeOffset enrolledAtUtc)
        : base(tenantId)
    {
        Hostname = hostname;
        HardwareId = hardwareId;
        Status = DeviceStatus.Active;
        EnrolledAtUtc = enrolledAtUtc;
        LastSeenAtUtc = enrolledAtUtc;
    }

    public string Hostname { get; private set; } = string.Empty;

    public string? DnsDomain { get; private set; }

    /// <summary>SMBIOS UUID; lets a re-imaged machine re-enrol into the same device record.</summary>
    public string? HardwareId { get; private set; }

    public string OsName { get; private set; } = string.Empty;

    public string OsVersion { get; private set; } = string.Empty;

    public string OsBuild { get; private set; } = string.Empty;

    public string AgentVersion { get; private set; } = string.Empty;

    public Guid? BranchId { get; private set; }

    public Guid? DepartmentId { get; private set; }

    public DeviceStatus Status { get; private set; }

    public DateTimeOffset EnrolledAtUtc { get; private set; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    public DateTimeOffset? LastBootUtc { get; private set; }

    public string CredentialHash { get; private set; } = string.Empty;

    public DateTimeOffset CredentialIssuedAtUtc { get; private set; }

    public Branch? Branch { get; private set; }

    public Department? Department { get; private set; }

    public bool IsActive => Status == DeviceStatus.Active;

    public static Device Enroll(Guid tenantId, string hostname, string? hardwareId, DateTimeOffset now) =>
        new(tenantId, Guard.NotNullOrWhiteSpace(hostname, "hostname", HostnameMaxLength), Guard.Optional(hardwareId, "hardwareId", ShortTextMaxLength), now);

    public void ReportInventory(string hostname, string? dnsDomain, string osName, string osVersion, string osBuild, string agentVersion)
    {
        Hostname = Guard.NotNullOrWhiteSpace(hostname, "hostname", HostnameMaxLength);
        DnsDomain = Guard.Optional(dnsDomain, "dnsDomain", HostnameMaxLength);
        OsName = Guard.Optional(osName, "osName", ShortTextMaxLength) ?? string.Empty;
        OsVersion = Guard.Optional(osVersion, "osVersion", ShortTextMaxLength) ?? string.Empty;
        OsBuild = Guard.Optional(osBuild, "osBuild", ShortTextMaxLength) ?? string.Empty;
        AgentVersion = Guard.Optional(agentVersion, "agentVersion", AgentVersionMaxLength) ?? string.Empty;
    }

    public void Touch(DateTimeOffset now, DateTimeOffset? lastBootUtc)
    {
        LastSeenAtUtc = now;
        if (lastBootUtc is { } boot)
        {
            LastBootUtc = boot;
        }
    }

    public void IssueCredential(string credentialHash, DateTimeOffset now)
    {
        CredentialHash = Guard.NotNullOrWhiteSpace(credentialHash, "credentialHash", 128);
        CredentialIssuedAtUtc = now;
    }

    /// <summary>Re-enrolment of a known machine (same hardware id): reactivates it.</summary>
    public void Reactivate(DateTimeOffset now)
    {
        Status = DeviceStatus.Active;
        EnrolledAtUtc = now;
    }

    public void PlaceIn(Guid? branchId, Guid? departmentId)
    {
        if (departmentId is not null && branchId is null)
        {
            throw new ValidationException("branchId", "A department can only be set together with its branch.");
        }

        BranchId = branchId;
        DepartmentId = departmentId;
    }

    public void Retire()
    {
        Status = DeviceStatus.Retired;
        CredentialHash = string.Empty;
    }
}
