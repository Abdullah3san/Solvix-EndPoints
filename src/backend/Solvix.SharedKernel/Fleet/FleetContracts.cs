namespace Solvix.SharedKernel.Fleet;

using Solvix.SharedKernel.Identity;

/// <summary>
/// Who is asking. Every fleet/command port receives the authenticated caller so the
/// implementing module can apply its own authorization (branch scoping, roles, RLS)
/// instead of trusting the caller. The AI layer never calls these ports with an
/// identity other than the signed-in user's.
/// </summary>
public sealed record FleetAccess(Guid TenantId, Guid UserId, TenantRole Role);

public enum DeviceConnectivity
{
    Online = 1,
    Offline = 2,
    Unknown = 3,
}

public enum DeviceSort
{
    Name = 1,
    LastSeen = 2,
    CpuUsageDesc = 3,
    MemoryUsageDesc = 4,
    FreeStorageAsc = 5,
}

public sealed record FleetScope(Guid? BranchId = null, IReadOnlyList<Guid>? DepartmentIds = null)
{
    public bool IsEmpty => BranchId is null && (DepartmentIds is null || DepartmentIds.Count == 0);
}

public sealed record DeviceSummary(
    Guid Id,
    string Name,
    DeviceConnectivity Status,
    DateTimeOffset? LastSeenUtc,
    Guid? BranchId,
    string? BranchName,
    Guid? DepartmentId,
    string? DepartmentName,
    string? OperatingSystem,
    string? OsVersion,
    string? Manufacturer,
    string? Model,
    double? CpuUsagePercent,
    double? MemoryUsagePercent,
    long? FreeStorageBytes,
    long? TotalStorageBytes);

public sealed record DeviceSearchCriteria
{
    public string? Text { get; init; }

    public DeviceConnectivity? Status { get; init; }

    public Guid? BranchId { get; init; }

    /// <summary>Match devices in any of these departments.</summary>
    public IReadOnlyList<Guid>? DepartmentIds { get; init; }

    public long? MaxFreeStorageBytes { get; init; }

    public double? MinCpuUsagePercent { get; init; }

    public double? MinMemoryUsagePercent { get; init; }

    public string? OperatingSystem { get; init; }

    public TimeSpan? OfflineFor { get; init; }

    public string? InstalledSoftware { get; init; }

    public bool? HasOutdatedSoftware { get; init; }

    public DeviceSort Sort { get; init; } = DeviceSort.Name;
}

public sealed record DiskInfo(string Name, string? FileSystem, long TotalBytes, long FreeBytes, bool Encrypted);

public sealed record DeviceDetail(
    DeviceSummary Summary,
    string? CpuModel,
    int? CpuCores,
    long? TotalMemoryBytes,
    IReadOnlyList<DiskInfo> Disks,
    string? BiosVersion,
    string? SerialNumber,
    string? IpAddress,
    string? AgentVersion,
    DateTimeOffset? LastBootUtc,
    bool PendingReboot);

public sealed record DeviceReference(Guid Id, string Name);

/// <summary>Result of resolving user/model supplied device references (names or ids) inside the caller's access.</summary>
public sealed record DeviceResolution(IReadOnlyList<DeviceSummary> Devices, IReadOnlyList<string> NotFound, IReadOnlyList<string> Ambiguous);

public sealed record BranchFleetCount(string BranchName, int Total, int Online, int Offline);

public sealed record FleetSummary(
    int Total,
    int Online,
    int Offline,
    int Unknown,
    int LowStorage,
    IReadOnlyDictionary<string, int> ByOperatingSystem,
    IReadOnlyList<BranchFleetCount> ByBranch);

public sealed record MetricStats(double Average, double P95, double Max);

public sealed record ProcessUsage(string Name, double CpuPercent, long MemoryBytes);

public sealed record PerformancePoint(DateTimeOffset TimestampUtc, double CpuPercent, double MemoryPercent);

public sealed record PerformanceReport(
    Guid DeviceId,
    string DeviceName,
    TimeSpan Window,
    int SampleCount,
    MetricStats Cpu,
    MetricStats Memory,
    MetricStats? DiskBusy,
    IReadOnlyList<ProcessUsage> TopProcesses,
    IReadOnlyList<PerformancePoint> Timeline);

public sealed record DiskHealth(string Name, string SmartStatus, int? WearPercent, int? ReallocatedSectors, int? TemperatureCelsius);

public sealed record BatteryHealth(int HealthPercent, int CycleCount);

public sealed record HardwareDiagnostics(
    Guid DeviceId,
    string DeviceName,
    IReadOnlyList<DiskHealth> Disks,
    BatteryHealth? Battery,
    bool MemoryErrorsDetected,
    int? CpuTemperatureCelsius,
    double UptimeDays,
    IReadOnlyList<string> RecentHardwareEvents);

public sealed record InstalledSoftware(
    string Name,
    string? Publisher,
    string Version,
    string? LatestVersion,
    bool IsOutdated,
    DateTimeOffset? InstalledOnUtc);

public sealed record SoftwareSearchCriteria(string? Name, string? Publisher, bool OutdatedOnly, FleetScope Scope);

public sealed record FleetSoftwareEntry(
    string Name,
    string? Publisher,
    string Version,
    string? LatestVersion,
    bool IsOutdated,
    int DeviceCount,
    IReadOnlyList<string> ExampleDevices);

public enum SecurityFindingKind
{
    NoAntivirus = 1,
    AntivirusOutdated = 2,
    FirewallDisabled = 3,
    DiskNotEncrypted = 4,
    OsUnsupported = 5,
    PatchesOverdue = 6,
    PendingReboot = 7,
    StaleAgent = 8,
}

public enum FindingSeverity
{
    Info = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    Critical = 5,
}

public sealed record SecurityFinding(SecurityFindingKind Kind, FindingSeverity Severity, Guid DeviceId, string DeviceName, string Detail);

public sealed record SecurityPosture(int TotalDevices, IReadOnlyList<SecurityFinding> Findings);

/// <summary>
/// Read access to the managed fleet. Implemented by the endpoint/device module and
/// MUST enforce tenant isolation and the caller's permissions. Returns no secrets.
/// </summary>
public interface IFleetQueryService
{
    /// <summary>False when no real fleet backend is connected (assistant then reports the limitation).</summary>
    bool IsAvailable { get; }

    Task<FleetSummary> GetSummaryAsync(FleetAccess access, FleetScope scope, CancellationToken cancellationToken);

    Task<(IReadOnlyList<DeviceSummary> Items, long Total)> SearchAsync(FleetAccess access, DeviceSearchCriteria criteria, int skip, int take, CancellationToken cancellationToken);

    Task<DeviceResolution> ResolveAsync(FleetAccess access, IReadOnlyList<string> references, CancellationToken cancellationToken);

    Task<DeviceDetail?> GetDetailAsync(FleetAccess access, Guid deviceId, CancellationToken cancellationToken);

    Task<PerformanceReport?> GetPerformanceAsync(FleetAccess access, Guid deviceId, TimeSpan window, CancellationToken cancellationToken);

    Task<HardwareDiagnostics?> GetHardwareDiagnosticsAsync(FleetAccess access, Guid deviceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<InstalledSoftware>?> GetInstalledSoftwareAsync(FleetAccess access, Guid deviceId, string? search, CancellationToken cancellationToken);

    Task<IReadOnlyList<FleetSoftwareEntry>> FindSoftwareAsync(FleetAccess access, SoftwareSearchCriteria criteria, int take, CancellationToken cancellationToken);

    Task<SecurityPosture> GetSecurityPostureAsync(FleetAccess access, FleetScope scope, CancellationToken cancellationToken);
}
