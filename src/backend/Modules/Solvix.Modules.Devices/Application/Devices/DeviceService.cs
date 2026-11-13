namespace Solvix.Modules.Devices.Application.Devices;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Application.Credentials;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Organization.Domain;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public interface IDeviceService
{
    Task<PagedResult<DeviceDto>> ListAsync(DeviceListQuery query, CancellationToken cancellationToken);

    Task<DeviceDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DeviceDto> PlaceAsync(Guid id, PlaceDeviceRequest request, CancellationToken cancellationToken);

    Task<DeviceDto> RetireAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class DeviceService(ISolvixDbContext db, IAuditTrail audit, IDeviceCredentialCache credentialCache) : IDeviceService
{
    public async Task<PagedResult<DeviceDto>> ListAsync(DeviceListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var devices = db.Set<Device>().AsNoTracking();
        if (!query.IncludeRetired)
        {
            devices = devices.Where(d => d.Status == DeviceStatus.Active);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            devices = devices.Where(d => EF.Functions.ILike(d.Hostname, pattern) || EF.Functions.ILike(d.OsName, pattern));
        }

        if (query.BranchId is { } branchId)
        {
            devices = devices.Where(d => d.BranchId == branchId);
        }

        if (query.DepartmentId is { } departmentId)
        {
            devices = devices.Where(d => d.DepartmentId == departmentId);
        }

        if (query.GroupId is { } groupId)
        {
            devices = devices.Where(d => db.Set<DeviceGroupMember>().Any(m => m.DeviceGroupId == groupId && m.DeviceId == d.Id));
        }

        var total = await devices.LongCountAsync(cancellationToken);
        var items = await devices.OrderBy(d => d.Hostname).ThenBy(d => d.Id)
            .Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        var groups = await GroupsOfAsync(items.Select(d => d.Id).ToList(), cancellationToken);
        return new PagedResult<DeviceDto>(
            items.Select(d => DeviceDto.From(d, groups.GetValueOrDefault(d.Id, []))).ToList(),
            page.SafePage,
            page.SafePageSize,
            total);
    }

    public async Task<DeviceDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await FindAsync(id, cancellationToken);
        var groups = await GroupsOfAsync([id], cancellationToken);
        return DeviceDto.From(device, groups.GetValueOrDefault(id, []));
    }

    public async Task<DeviceDto> PlaceAsync(Guid id, PlaceDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await FindAsync(id, cancellationToken);
        var (branchId, departmentId) = await ResolvePlacementAsync(db, request.BranchId, request.DepartmentId, cancellationToken);
        device.PlaceIn(branchId, departmentId);
        audit.Record("device.placed", nameof(Device), device.Id, $"Device '{device.Hostname}' moved to branch {branchId?.ToString() ?? "-"} / department {departmentId?.ToString() ?? "-"}.", new { branchId, departmentId });
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeviceDto> RetireAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await FindAsync(id, cancellationToken);
        device.Retire();
        audit.Record("device.retired", nameof(Device), device.Id, $"Device '{device.Hostname}' retired; its credential is revoked.");
        await db.SaveChangesAsync(cancellationToken);
        await credentialCache.InvalidateAsync(device.Id, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    /// <summary>
    /// Validates a branch/department pair inside the current tenant. A department
    /// alone implies its branch; a department of another branch is rejected.
    /// </summary>
    internal static async Task<(Guid? BranchId, Guid? DepartmentId)> ResolvePlacementAsync(
        ISolvixDbContext db, Guid? branchId, Guid? departmentId, CancellationToken cancellationToken)
    {
        if (departmentId is { } deptId)
        {
            var department = await db.Set<Department>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == deptId, cancellationToken)
                ?? throw new ValidationException("departmentId", "Department not found.");
            if (branchId is not null && branchId != department.BranchId)
            {
                throw new ValidationException("departmentId", "The department belongs to a different branch.");
            }

            return (department.BranchId, deptId);
        }

        if (branchId is { } bId && !await db.Set<Branch>().AnyAsync(b => b.Id == bId, cancellationToken))
        {
            throw new ValidationException("branchId", "Branch not found.");
        }

        return (branchId, null);
    }

    private async Task<Device> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<Device>().FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
        ?? throw new NotFoundException("Device", id);

    private async Task<Dictionary<Guid, IReadOnlyList<Guid>>> GroupsOfAsync(List<Guid> deviceIds, CancellationToken cancellationToken)
    {
        var rows = await db.Set<DeviceGroupMember>().AsNoTracking()
            .Where(m => deviceIds.Contains(m.DeviceId))
            .Select(m => new { m.DeviceId, m.DeviceGroupId })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.DeviceId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.DeviceGroupId).ToList());
    }
}
