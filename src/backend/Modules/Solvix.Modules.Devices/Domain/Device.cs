namespace Solvix.Modules.Devices.Domain;

using Solvix.Agent.Contracts;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

public enum DevicePlatform
{
    Windows = 1,
}

/// <summary>Administrative lifecycle state. Connectivity (online/offline) is derived from <see cref="Device.LastSeenAtUtc"/>.</summary>
public enum DeviceStatus
{
    Active = 1,

    /// <summary>Decommissioned: credentials revoked, re-enrollment blocked until the record is deleted.</summary>
    Retired = 2,
}

/// <summary>
/// An enrolled endpoint. Identity is the server-assigned <see cref="Entity.Id"/>
/// bound to X.509 credentials; <see cref="HardwareFingerprint"/> only serves
/// duplicate detection within a tenant. Inventory-derived columns are a
/// denormalised summary of the latest <see cref="DeviceInventory"/> for listing
/// and filtering.
/// </summary>
public sealed class Device : TenantEntity
{
    public const int HostnameMaxLength = 255;
    public const int DisplayNameMaxLength = 200;
    public const int NotesMaxLength = 2000;
    public const int ShortTextMaxLength = 128;
    public const int VersionMaxLength = Solvix.Agent.Contracts.AgentVersion.MaxLength;
    public const int FingerprintLength = 64;

    private Device()
    {
    }

    private Device(Guid tenantId, DevicePlatform platform, string hostname)
        : base(tenantId)
    {
        Platform = platform;
        Hostname = hostname;
        Status = DeviceStatus.Active;
    }

    public string Hostname { get; private set; } = string.Empty;

    public string? DisplayName { get; private set; }

    public string? Notes { get; private set; }

    public DevicePlatform Platform { get; private set; }

    public DeviceStatus Status { get; private set; }

    /// <summary>SMBIOS UUID as reported by the agent (informational).</summary>
    public string? HardwareId { get; private set; }

    /// <summary>SHA-256 over normalised hardware id + serial; null when the hardware reports placeholder values.</summary>
    public string? HardwareFingerprint { get; private set; }

    public string? SerialNumber { get; private set; }

    public string? Manufacturer { get; private set; }

    public string? Model { get; private set; }

    public string? DomainName { get; private set; }

    public string? OsName { get; private set; }

    public string? OsVersion { get; private set; }

    public string? OsBuild { get; private set; }

    public string? OsDisplayVersion { get; private set; }

    public string? Architecture { get; private set; }

    public string? ProcessorName { get; private set; }

    public long? TotalMemoryBytes { get; private set; }

    public DateTimeOffset? LastBootAtUtc { get; private set; }

    public List<string> IpAddresses { get; private set; } = [];

    public List<string> MacAddresses { get; private set; } = [];

    public string AgentVersion { get; private set; } = string.Empty;

    public DateTimeOffset? AgentVersionChangedAtUtc { get; private set; }

    public AgentHealthStatus? HealthStatus { get; private set; }

    /// <summary>Latest <see cref="AgentHealthReport"/> as JSON (jsonb).</summary>
    public string? AgentHealthJson { get; private set; }

    public DateTimeOffset EnrolledAtUtc { get; private set; }

    public DateTimeOffset LastEnrolledAtUtc { get; private set; }

    public int EnrollmentCount { get; private set; }

    public Guid? EnrollmentTokenId { get; private set; }

    public DateTimeOffset? LastSeenAtUtc { get; private set; }

    public DateTimeOffset? LastInventoryAtUtc { get; private set; }

    public DateTimeOffset? InventoryRequestedAtUtc { get; private set; }

    public DateTimeOffset? RetiredAtUtc { get; private set; }

    public Guid? AssignedUserId { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid? DepartmentId { get; private set; }

    public bool IsInventoryRequested =>
        InventoryRequestedAtUtc is { } requested && (LastInventoryAtUtc is null || LastInventoryAtUtc < requested);

    public static Device Enroll(
        Guid tenantId,
        DevicePlatform platform,
        DeviceIdentityInfo identity,
        string? hardwareFingerprint,
        Guid enrollmentTokenId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var device = new Device(tenantId, platform, Truncate(Guard.NotNullOrWhiteSpace(identity.Hostname, "hostname"), HostnameMaxLength)!)
        {
            HardwareFingerprint = hardwareFingerprint,
            EnrolledAtUtc = now,
        };
        device.ApplyEnrollment(identity, enrollmentTokenId, now);
        return device;
    }

    /// <summary>
    /// The same physical machine enrolled again (OS reinstall, agent reinstall).
    /// The record, its groups and assignment are kept; the caller revokes the
    /// previous credentials.
    /// </summary>
    public void ReEnroll(DeviceIdentityInfo identity, Guid enrollmentTokenId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (Status == DeviceStatus.Retired)
        {
            throw new ConflictException("This device has been retired. Delete the device record before enrolling it again.", "device_retired");
        }

        ApplyEnrollment(identity, enrollmentTokenId, now);
    }

    public void RecordHeartbeat(string agentVersion, AgentHealthStatus healthStatus, string healthJson, string? osBuild, DateTimeOffset now)
    {
        SetAgentVersion(agentVersion, now);
        HealthStatus = healthStatus;
        AgentHealthJson = healthJson;
        if (!string.IsNullOrWhiteSpace(osBuild))
        {
            OsBuild = Truncate(osBuild, 32);
        }

        LastSeenAtUtc = now;
    }

    public void ApplyInventory(InventoryReport report, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(report);
        Hostname = Truncate(report.Hostname, HostnameMaxLength) ?? Hostname;
        SetAgentVersion(report.AgentVersion, now);

        if (report.System is { } system)
        {
            Manufacturer = Truncate(system.Manufacturer, ShortTextMaxLength) ?? Manufacturer;
            Model = Truncate(system.Model, ShortTextMaxLength) ?? Model;
            SerialNumber = Truncate(system.SerialNumber, ShortTextMaxLength) ?? SerialNumber;
            DomainName = Truncate(system.DomainName, HostnameMaxLength);
        }

        if (report.OperatingSystem is { } os)
        {
            OsName = Truncate(os.Name, ShortTextMaxLength) ?? OsName;
            OsVersion = Truncate(os.Version, 32) ?? OsVersion;
            OsBuild = Truncate(os.UpdateBuildRevision is { } ubr && os.BuildNumber is not null ? $"{os.BuildNumber}.{ubr}" : os.BuildNumber, 32) ?? OsBuild;
            OsDisplayVersion = Truncate(os.DisplayVersion, 32);
            Architecture = Truncate(os.Architecture, 16) ?? Architecture;
            LastBootAtUtc = os.LastBootUpTimeUtc ?? LastBootAtUtc;
        }

        if (report.Processors.Count > 0)
        {
            ProcessorName = Truncate(report.Processors[0].Name?.Trim(), 256);
        }

        TotalMemoryBytes = report.Memory?.TotalPhysicalBytes ?? TotalMemoryBytes;

        var adapters = report.NetworkAdapters.Where(a => !string.Equals(a.Status, "Down", StringComparison.OrdinalIgnoreCase)).ToList();
        IpAddresses = adapters
            .SelectMany(a => a.IPv4Addresses.Concat(a.IPv6Addresses))
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();
        MacAddresses = report.NetworkAdapters
            .Where(a => a.IsPhysical && !string.IsNullOrWhiteSpace(a.MacAddress))
            .Select(a => a.MacAddress!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToList();

        LastInventoryAtUtc = now;
        LastSeenAtUtc = now;
    }

    public void UpdateDetails(string? displayName, string? notes)
    {
        DisplayName = Guard.Optional(displayName, "displayName", DisplayNameMaxLength);
        Notes = Guard.Optional(notes, "notes", NotesMaxLength);
    }

    public void Assign(Guid? userId, Guid? branchId, Guid? departmentId)
    {
        AssignedUserId = userId;
        BranchId = branchId;
        DepartmentId = departmentId;
    }

    public void RequestInventory(DateTimeOffset now)
    {
        EnsureActive();
        InventoryRequestedAtUtc = now;
    }

    public void Retire(DateTimeOffset now)
    {
        if (Status == DeviceStatus.Retired)
        {
            return;
        }

        Status = DeviceStatus.Retired;
        RetiredAtUtc = now;
        InventoryRequestedAtUtc = null;
    }

    public void EnsureActive()
    {
        if (Status != DeviceStatus.Active)
        {
            throw new ConflictException("The device is retired.", "device_retired");
        }
    }

    private void ApplyEnrollment(DeviceIdentityInfo identity, Guid enrollmentTokenId, DateTimeOffset now)
    {
        Hostname = Truncate(identity.Hostname.Trim(), HostnameMaxLength)!;
        HardwareId = Truncate(identity.HardwareId?.Trim(), 64);
        SerialNumber = Truncate(identity.SerialNumber?.Trim(), ShortTextMaxLength);
        Manufacturer = Truncate(identity.Manufacturer?.Trim(), ShortTextMaxLength);
        Model = Truncate(identity.Model?.Trim(), ShortTextMaxLength);
        DomainName = Truncate(identity.DomainName?.Trim(), HostnameMaxLength);
        OsName = Truncate(identity.OsName?.Trim(), ShortTextMaxLength);
        OsVersion = Truncate(identity.OsVersion?.Trim(), 32);
        OsBuild = Truncate(identity.OsBuild?.Trim(), 32);
        Architecture = Truncate(identity.Architecture?.Trim(), 16);
        SetAgentVersion(identity.AgentVersion, now);
        EnrollmentTokenId = enrollmentTokenId;
        LastEnrolledAtUtc = now;
        LastSeenAtUtc = now;
        EnrollmentCount++;
    }

    private void SetAgentVersion(string agentVersion, DateTimeOffset now)
    {
        var version = Guard.NotNullOrWhiteSpace(agentVersion, "agentVersion", VersionMaxLength);
        if (!string.Equals(version, AgentVersion, StringComparison.Ordinal))
        {
            AgentVersion = version;
            AgentVersionChangedAtUtc = now;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
