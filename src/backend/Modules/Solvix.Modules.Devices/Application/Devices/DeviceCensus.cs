namespace Solvix.Modules.Devices.Application.Devices;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

internal sealed class DeviceCensus(ISolvixDbContext db, ITenantContext tenantContext) : IDeviceCensus
{
    public async Task<IReadOnlyDictionary<Guid, int>> CountActiveAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        if (!tenantContext.IsPlatformScope)
        {
            throw new ForbiddenException("Cross-tenant device counts require platform scope.");
        }

        if (tenantIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var counts = await db.Set<Device>()
            .IgnoreQueryFilters()
            .Where(d => tenantIds.Contains(d.TenantId) && d.Status == DeviceStatus.Active)
            .GroupBy(d => d.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return counts.ToDictionary(c => c.TenantId, c => c.Count);
    }
}
