namespace Solvix.Agent.Windows.Inventory;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Inventory;

/// <summary>Hardware inventory: manufacturer, model, serial, SMBIOS UUID, chassis, domain.</summary>
[SupportedOSPlatform("windows")]
public sealed class SystemCollector : IInventoryCollector
{
    public string Name => "system";

    public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var cs = Wmi.Query(Wmi.Cimv2, "Win32_ComputerSystem", "Manufacturer", "Model", "Domain", "PartOfDomain", "Workgroup", "SystemSKUNumber", "SystemFamily").FirstOrDefault();
        var product = Wmi.Query(Wmi.Cimv2, "Win32_ComputerSystemProduct", "UUID", "IdentifyingNumber").FirstOrDefault();
        var bios = Wmi.Query(Wmi.Cimv2, "Win32_BIOS", "SerialNumber").FirstOrDefault();
        var enclosure = Wmi.Query(Wmi.Cimv2, "Win32_SystemEnclosure", "ChassisTypes").FirstOrDefault();
        var partOfDomain = cs?.Bool("PartOfDomain");
        builder.System = new SystemInfo
        {
            Manufacturer = cs?.Text("Manufacturer"),
            Model = cs?.Text("Model"),
            SerialNumber = bios?.Text("SerialNumber") ?? product?.Text("IdentifyingNumber"),
            HardwareId = product?.Text("UUID"),
            SystemSku = cs?.Text("SystemSKUNumber"),
            SystemFamily = cs?.Text("SystemFamily"),
            ChassisType = enclosure is not null && enclosure.TryGetValue("ChassisTypes", out var types) && types is ushort[] { Length: > 0 } t ? Chassis(t[0]) : null,
            PartOfDomain = partOfDomain,
            DomainName = partOfDomain == true ? cs?.Text("Domain") : null,
            Workgroup = partOfDomain == true ? null : cs?.Text("Workgroup"),
        };
        return Task.CompletedTask;
    }

    internal static string Chassis(ushort value) => value switch
    {
        3 or 4 or 5 or 6 or 7 or 15 or 16 or 35 => "Desktop",
        8 or 9 or 10 or 14 => "Laptop",
        13 => "AllInOne",
        30 or 31 or 32 => "Tablet",
        17 or 23 or 28 => "Server",
        _ => "Other",
    };
}

/// <summary>BIOS / firmware information.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class BiosCollector : IInventoryCollector
{
    public string Name => "bios";

    public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var bios = Wmi.Query(Wmi.Cimv2, "Win32_BIOS", "Manufacturer", "SMBIOSBIOSVersion", "SMBIOSMajorVersion", "SMBIOSMinorVersion", "SerialNumber", "ReleaseDate").FirstOrDefault();
        builder.Bios = new BiosInfo
        {
            Manufacturer = bios?.Text("Manufacturer"),
            Version = bios?.Text("SMBIOSBIOSVersion"),
            SmbiosVersion = bios?.Int("SMBIOSMajorVersion") is { } major ? $"{major}.{bios.Int("SMBIOSMinorVersion") ?? 0}" : null,
            SerialNumber = bios?.Text("SerialNumber"),
            ReleaseDate = bios?.Date("ReleaseDate"),
            FirmwareType = FirmwareType(),
            SecureBootEnabled = SecureBoot(),
        };
        return Task.CompletedTask;
    }

    private static string? FirmwareType() =>
        GetFirmwareType(out var type) ? type switch { 1 => "Legacy", 2 => "UEFI", _ => null } : null;

    private static bool? SecureBoot()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return key?.GetValue("UEFISecureBootEnabled") is int value ? value == 1 : null;
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFirmwareType(out int firmwareType);
}

/// <summary>Processor information.</summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessorCollector : IInventoryCollector
{
    public string Name => "processors";

    public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var cpu in Wmi.Query(Wmi.Cimv2, "Win32_Processor", "Name", "Manufacturer", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed", "Architecture", "SocketDesignation", "ProcessorId"))
        {
            builder.Processors.Add(new ProcessorInfo
            {
                Name = cpu.Text("Name"),
                Manufacturer = cpu.Text("Manufacturer"),
                Cores = cpu.Int("NumberOfCores"),
                LogicalProcessors = cpu.Int("NumberOfLogicalProcessors"),
                MaxClockSpeedMhz = cpu.Int("MaxClockSpeed"),
                Architecture = cpu.Int("Architecture") switch { 0 => "x86", 5 => "ARM", 9 => "x64", 12 => "ARM64", _ => null },
                Socket = cpu.Text("SocketDesignation"),
                ProcessorId = cpu.Text("ProcessorId"),
            });
        }

        return Task.CompletedTask;
    }
}

/// <summary>Memory information: total/available physical memory and modules.</summary>
[SupportedOSPlatform("windows")]
public sealed class MemoryCollector : IInventoryCollector
{
    public string Name => "memory";

    public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var cs = Wmi.Query(Wmi.Cimv2, "Win32_ComputerSystem", "TotalPhysicalMemory").FirstOrDefault();
        var os = Wmi.Query(Wmi.Cimv2, "Win32_OperatingSystem", "FreePhysicalMemory").FirstOrDefault();
        var modules = Wmi.Query(Wmi.Cimv2, "Win32_PhysicalMemory", "Capacity", "ConfiguredClockSpeed", "Speed", "Manufacturer", "PartNumber", "SerialNumber", "DeviceLocator", "FormFactor")
            .Select(m => new MemoryModuleInfo
            {
                CapacityBytes = m.Long("Capacity"),
                SpeedMhz = m.Int("ConfiguredClockSpeed") ?? m.Int("Speed"),
                Manufacturer = m.Text("Manufacturer"),
                PartNumber = m.Text("PartNumber"),
                SerialNumber = m.Text("SerialNumber"),
                Slot = m.Text("DeviceLocator"),
                FormFactor = m.Int("FormFactor") switch { 8 => "DIMM", 12 => "SODIMM", 0 => null, var f => f?.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            })
            .ToList();
        builder.Memory = new MemoryInfo
        {
            TotalPhysicalBytes = cs?.Long("TotalPhysicalMemory"),
            AvailablePhysicalBytes = os?.Long("FreePhysicalMemory") * 1024,
            Modules = modules,
        };
        return Task.CompletedTask;
    }
}

/// <summary>Physical disk inventory (Storage Management API, Win32_DiskDrive fallback).</summary>
[SupportedOSPlatform("windows")]
public sealed class DiskCollector : IInventoryCollector
{
    public string Name => "disks";

    public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        try
        {
            foreach (var d in Wmi.Query(Wmi.Storage, "MSFT_PhysicalDisk", "FriendlyName", "SerialNumber", "Size", "MediaType", "BusType", "HealthStatus", "FirmwareVersion"))
            {
                builder.Disks.Add(new PhysicalDiskInfo
                {
                    Model = d.Text("FriendlyName"),
                    SerialNumber = d.Text("SerialNumber"),
                    SizeBytes = d.Long("Size"),
                    MediaType = d.Int("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" },
                    BusType = BusType(d.Int("BusType")),
                    HealthStatus = d.Int("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null },
                    FirmwareVersion = d.Text("FirmwareVersion"),
                });
            }

            return Task.CompletedTask;
        }
        catch (System.Management.ManagementException)
        {
            // Storage namespace unavailable (e.g. restricted Server Core): fall back.
        }

        foreach (var d in Wmi.Query(Wmi.Cimv2, "Win32_DiskDrive", "Model", "SerialNumber", "Size", "InterfaceType", "MediaType"))
        {
            builder.Disks.Add(new PhysicalDiskInfo
            {
                Model = d.Text("Model"),
                SerialNumber = d.Text("SerialNumber"),
                SizeBytes = d.Long("Size"),
                BusType = d.Text("InterfaceType"),
                MediaType = "Unspecified",
            });
        }

        return Task.CompletedTask;
    }

    internal static string? BusType(int? value) => value switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 6 => "FibreChannel", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "FileBackedVirtual", 16 => "StorageSpaces", 17 => "NVMe",
        null => null,
        _ => "Other",
    };
}
