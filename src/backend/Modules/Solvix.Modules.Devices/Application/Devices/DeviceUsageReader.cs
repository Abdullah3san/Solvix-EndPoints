namespace Solvix.Modules.Devices.Application.Devices;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Billing;
using Solvix.SharedKernel.Persistence;

/// <summary>Billable devices are the active (non-retired) devices of the ambient tenant; retiring a device frees its slot.</summary>
internal sealed class DeviceUsageReader(ISolvixDbContext db) : IDeviceUsageReader
{
    public Task<int> CountBillableDevicesAsync(CancellationToken cancellationToken) =>
        db.Set<Device>().CountAsync(d => d.Status == DeviceStatus.Active, cancellationToken);
}
