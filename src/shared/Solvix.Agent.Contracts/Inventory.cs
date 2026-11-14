namespace Solvix.Agent.Contracts;

/// <summary>
/// Full inventory snapshot of a Windows device. Every section is optional so
/// that a failure in one collector never prevents the others from reporting.
/// </summary>
public sealed record InventoryReport
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxSoftwareEntries = 5000;
    public const int MaxNetworkAdapters = 64;
    public const int MaxDisks = 64;
    public const int MaxVolumes = 128;
    public const int MaxProcessors = 16;
    public const int MaxMemoryModules = 64;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required DateTimeOffset CollectedAtUtc { get; init; }

    public required string AgentVersion { get; init; }

    public required string Hostname { get; init; }

    /// <summary>SHA-256 of the non-volatile content, computed by the agent (change detection).</summary>
    public string? ContentHash { get; init; }

    public SystemInfo? System { get; init; }

    public BiosInfo? Bios { get; init; }

    public OperatingSystemInfo? OperatingSystem { get; init; }

    public IReadOnlyList<ProcessorInfo> Processors { get; init; } = [];

    public MemoryInfo? Memory { get; init; }

    public IReadOnlyList<PhysicalDiskInfo> Disks { get; init; } = [];

    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = [];

    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } = [];

    public IReadOnlyList<InstalledApplication> Software { get; init; } = [];

    /// <summary>Collector failures (section name + message); informational.</summary>
    public IReadOnlyList<HealthIssue> CollectionIssues { get; init; } = [];
}

public sealed record SystemInfo
{
    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    /// <summary>SMBIOS system UUID.</summary>
    public string? HardwareId { get; init; }

    public string? SystemSku { get; init; }

    public string? SystemFamily { get; init; }

    public string? ChassisType { get; init; }

    public string? DomainName { get; init; }

    public bool? PartOfDomain { get; init; }

    public string? Workgroup { get; init; }
}

public sealed record BiosInfo
{
    public string? Manufacturer { get; init; }

    public string? Version { get; init; }

    public string? SmbiosVersion { get; init; }

    public string? SerialNumber { get; init; }

    public DateTimeOffset? ReleaseDate { get; init; }

    /// <summary>"UEFI" or "Legacy".</summary>
    public string? FirmwareType { get; init; }

    public bool? SecureBootEnabled { get; init; }
}

public sealed record OperatingSystemInfo
{
    /// <summary>Friendly product name, e.g. "Windows 11 Pro" or "Windows Server 2022 Standard".</summary>
    public string? Name { get; init; }

    /// <summary>Product family: Windows 10, Windows 11, Windows Server 2019, ...</summary>
    public string? Family { get; init; }

    public string? Edition { get; init; }

    /// <summary>Kernel version, e.g. 10.0.22631.</summary>
    public string? Version { get; init; }

    /// <summary>Marketing version, e.g. 23H2.</summary>
    public string? DisplayVersion { get; init; }

    public string? BuildNumber { get; init; }

    /// <summary>Update build revision (patch level).</summary>
    public int? UpdateBuildRevision { get; init; }

    public string? Architecture { get; init; }

    /// <summary>Workstation, DomainController or Server.</summary>
    public string? ProductType { get; init; }

    public bool? IsSupported { get; init; }

    public DateTimeOffset? InstallDateUtc { get; init; }

    public DateTimeOffset? LastBootUpTimeUtc { get; init; }

    public string? Locale { get; init; }

    public string? TimeZone { get; init; }
}

public sealed record ProcessorInfo
{
    public string? Name { get; init; }

    public string? Manufacturer { get; init; }

    public int? Cores { get; init; }

    public int? LogicalProcessors { get; init; }

    public int? MaxClockSpeedMhz { get; init; }

    public string? Architecture { get; init; }

    public string? Socket { get; init; }

    public string? ProcessorId { get; init; }
}

public sealed record MemoryInfo
{
    public long? TotalPhysicalBytes { get; init; }

    /// <summary>Volatile; excluded from the content hash.</summary>
    public long? AvailablePhysicalBytes { get; init; }

    public IReadOnlyList<MemoryModuleInfo> Modules { get; init; } = [];
}

public sealed record MemoryModuleInfo
{
    public long? CapacityBytes { get; init; }

    public int? SpeedMhz { get; init; }

    public string? Manufacturer { get; init; }

    public string? PartNumber { get; init; }

    public string? SerialNumber { get; init; }

    public string? Slot { get; init; }

    public string? FormFactor { get; init; }
}

public sealed record PhysicalDiskInfo
{
    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>SSD, HDD, SCM or Unspecified.</summary>
    public string? MediaType { get; init; }

    /// <summary>NVMe, SATA, SAS, USB, ...</summary>
    public string? BusType { get; init; }

    public string? HealthStatus { get; init; }

    public string? FirmwareVersion { get; init; }
}

public sealed record VolumeInfo
{
    public string? DriveLetter { get; init; }

    public string? Label { get; init; }

    public string? FileSystem { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>Volatile; excluded from the content hash.</summary>
    public long? FreeBytes { get; init; }

    /// <summary>Fixed, Removable, Network, CdRom, Ram.</summary>
    public string? DriveType { get; init; }

    public bool? BitLockerProtected { get; init; }
}

public sealed record NetworkAdapterInfo
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Upper-case, colon separated (AA:BB:CC:DD:EE:FF).</summary>
    public string? MacAddress { get; init; }

    public string? AdapterType { get; init; }

    public long? SpeedBitsPerSecond { get; init; }

    /// <summary>Up, Down, ...</summary>
    public string? Status { get; init; }

    public bool? DhcpEnabled { get; init; }

    public bool IsPhysical { get; init; }

    public IReadOnlyList<string> IPv4Addresses { get; init; } = [];

    public IReadOnlyList<string> IPv6Addresses { get; init; } = [];

    public IReadOnlyList<string> Gateways { get; init; } = [];

    public IReadOnlyList<string> DnsServers { get; init; } = [];
}

public sealed record InstalledApplication
{
    public required string Name { get; init; }

    public string? Version { get; init; }

    public string? Publisher { get; init; }

    public DateOnly? InstallDate { get; init; }

    /// <summary>x64, x86 or User.</summary>
    public string? Scope { get; init; }

    public string? InstallLocation { get; init; }

    public long? EstimatedSizeBytes { get; init; }
}
