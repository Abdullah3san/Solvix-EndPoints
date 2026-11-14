namespace Solvix.Agent.Core.Inventory;

using System.Net.NetworkInformation;
using System.Net.Sockets;
using Solvix.Agent.Contracts;

/// <summary>Network interface inventory via the cross-platform NetworkInformation API.</summary>
public sealed class NetworkInventoryCollector : IInventoryCollector
{
    private static readonly string[] VirtualMarkers =
        ["virtual", "hyper-v", "vmware", "virtualbox", "tap-", "tap ", "vpn", "wan miniport", "bluetooth", "loopback", "teredo", "isatap", "pseudo"];

    public string Name => "network";

    public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var properties = nic.GetIPProperties();
            builder.NetworkAdapters.Add(new NetworkAdapterInfo
            {
                Name = nic.Name,
                Description = nic.Description,
                MacAddress = FormatMac(nic.GetPhysicalAddress()),
                AdapterType = nic.NetworkInterfaceType.ToString(),
                SpeedBitsPerSecond = SafeSpeed(nic),
                Status = nic.OperationalStatus.ToString(),
                DhcpEnabled = DhcpEnabled(properties),
                IsPhysical = IsPhysical(nic),
                IPv4Addresses = properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList(),
                IPv6Addresses = properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.Address.ToString()).ToList(),
                Gateways = properties.GatewayAddresses.Select(g => g.Address.ToString()).Where(g => g != "0.0.0.0").ToList(),
                DnsServers = properties.DnsAddresses.Select(d => d.ToString()).ToList(),
            });
        }

        return Task.CompletedTask;
    }

    internal static string? FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 6 && bytes.Any(b => b != 0) ? string.Join(':', bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))) : null;
    }

    internal static bool IsPhysical(NetworkInterface nic) =>
        nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
        && !VirtualMarkers.Any(m => nic.Description.Contains(m, StringComparison.OrdinalIgnoreCase) || nic.Name.Contains(m, StringComparison.OrdinalIgnoreCase))
        && FormatMac(nic.GetPhysicalAddress()) is not null;

    private static long? SafeSpeed(NetworkInterface nic)
    {
        try
        {
            return nic.Speed > 0 ? nic.Speed : null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static bool? DhcpEnabled(IPInterfaceProperties properties)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return properties.GetIPv4Properties()?.IsDhcpEnabled;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}

/// <summary>Logical volumes via DriveInfo (free space, file system, size).</summary>
public sealed class VolumeInventoryCollector : IInventoryCollector
{
    public string Name => "volumes";

    public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!drive.IsReady || drive.DriveType is DriveType.Unknown or DriveType.NoRootDirectory)
            {
                continue;
            }

            if (!OperatingSystem.IsWindows() && drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            builder.Volumes.Add(new VolumeInfo
            {
                DriveLetter = OperatingSystem.IsWindows() ? drive.Name.TrimEnd('\\') : drive.Name,
                Label = Safe(() => drive.VolumeLabel),
                FileSystem = Safe(() => drive.DriveFormat),
                SizeBytes = drive.TotalSize,
                FreeBytes = drive.AvailableFreeSpace,
                DriveType = drive.DriveType.ToString(),
            });
        }

        return Task.CompletedTask;
    }

    private static string? Safe(Func<string> read)
    {
        try
        {
            var value = read();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
