namespace Solvix.Agent.Core.Inventory;

using Solvix.Agent.Contracts;

/// <summary>
/// Mutable accumulator filled by the inventory modules. Each module owns its
/// sections; a failing module never prevents the others from reporting.
/// </summary>
public sealed class InventoryBuilder
{
    public SystemInfo? System { get; set; }

    public BiosInfo? Bios { get; set; }

    public OperatingSystemInfo? OperatingSystem { get; set; }

    public List<ProcessorInfo> Processors { get; } = [];

    public MemoryInfo? Memory { get; set; }

    public List<PhysicalDiskInfo> Disks { get; } = [];

    public List<VolumeInfo> Volumes { get; } = [];

    public List<NetworkAdapterInfo> NetworkAdapters { get; } = [];

    public List<InstalledApplication> Software { get; } = [];
}

/// <summary>One inventory module (hardware, OS, software, network, disks, CPU, memory, ...).</summary>
public interface IInventoryCollector
{
    /// <summary>Stable module name used in collection issues.</summary>
    string Name { get; }

    Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken);
}
