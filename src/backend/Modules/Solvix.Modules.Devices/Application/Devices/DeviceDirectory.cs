namespace Solvix.Modules.Devices.Application.Devices;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Persistence;

internal sealed class DeviceDirectory(ISolvixDbContext db) : IDeviceDirectory
{
    public async Task<IReadOnlyDictionary<Guid, DeviceSummary>> FindAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken)
    {
        if (deviceIds.Count == 0)
        {
            return new Dictionary<Guid, DeviceSummary>();
        }

        return await db.Set<Device>().AsNoTracking()
            .Where(d => deviceIds.Contains(d.Id))
            .Select(d => new DeviceSummary(d.Id, d.Hostname, d.DisplayName))
            .ToDictionaryAsync(d => d.Id, cancellationToken);
    }
}
