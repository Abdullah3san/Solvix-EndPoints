namespace Solvix.Agent.Core.Inventory;

using System.Runtime.InteropServices;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;

public interface IDeviceIdentityProvider
{
    Task<DeviceIdentityInfo> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Device identity module: derives enrollment identity facts from the hardware/OS inventory modules.</summary>
public sealed class DeviceIdentityProvider(IEnumerable<IInventoryCollector> collectors) : IDeviceIdentityProvider
{
    private static readonly HashSet<string> IdentityModules = new(StringComparer.Ordinal) { "system", "operating-system" };

    public async Task<DeviceIdentityInfo> GetAsync(CancellationToken cancellationToken)
    {
        var builder = new InventoryBuilder();
        foreach (var collector in collectors.Where(c => IdentityModules.Contains(c.Name)))
        {
            try
            {
                await collector.CollectAsync(builder, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Enrollment proceeds with less information (no duplicate detection).
            }
        }

        return new DeviceIdentityInfo
        {
            HardwareId = builder.System?.HardwareId,
            SerialNumber = builder.System?.SerialNumber,
            Hostname = Environment.MachineName,
            DomainName = builder.System?.DomainName,
            Manufacturer = builder.System?.Manufacturer,
            Model = builder.System?.Model,
            OsName = builder.OperatingSystem?.Name ?? RuntimeInformation.OSDescription,
            OsVersion = builder.OperatingSystem?.Version ?? Environment.OSVersion.Version.ToString(),
            OsBuild = builder.OperatingSystem?.BuildNumber,
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            AgentVersion = AgentInfo.Version,
        };
    }
}
