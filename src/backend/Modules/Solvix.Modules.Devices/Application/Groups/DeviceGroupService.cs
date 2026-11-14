namespace Solvix.Modules.Devices.Application.Groups;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IDeviceGroupService
{
    Task<PagedResult<DeviceGroupDto>> ListAsync(DeviceGroupListQuery query, CancellationToken cancellationToken);

    Task<DeviceGroupDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DeviceGroupDto> CreateAsync(CreateDeviceGroupRequest request, CancellationToken cancellationToken);

    Task<DeviceGroupDto> UpdateAsync(Guid id, UpdateDeviceGroupRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task<DeviceGroupDto> AddDevicesAsync(Guid id, AddGroupDevicesRequest request, CancellationToken cancellationToken);

    Task<DeviceGroupDto> RemoveDeviceAsync(Guid id, Guid deviceId, CancellationToken cancellationToken);
}

internal sealed class DeviceGroupService(ISolvixDbContext db, ITenantContext tenantContext) : IDeviceGroupService
{
    public async Task<PagedResult<DeviceGroupDto>> ListAsync(DeviceGroupListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var groups = db.Set<DeviceGroup>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            groups = groups.Where(g => EF.Functions.ILike(g.Name, pattern));
        }

        var total = await groups.LongCountAsync(cancellationToken);
        var items = await Project(groups.OrderBy(g => g.Name).Skip(page.Skip).Take(page.SafePageSize)).ToListAsync(cancellationToken);
        return new PagedResult<DeviceGroupDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<DeviceGroupDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await Project(db.Set<DeviceGroup>().AsNoTracking().Where(g => g.Id == id)).FirstOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException("Device group", id);

    public async Task<DeviceGroupDto> CreateAsync(CreateDeviceGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var group = DeviceGroup.Create(tenantContext.RequireTenantId(), request.Name, request.Description);
        await EnsureUniqueNameAsync(group.NormalizedName, null, cancellationToken);
        db.Set<DeviceGroup>().Add(group);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(group.Id, cancellationToken);
    }

    public async Task<DeviceGroupDto> UpdateAsync(Guid id, UpdateDeviceGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var group = await FindAsync(id, cancellationToken);
        await EnsureUniqueNameAsync(DeviceGroup.Normalize(request.Name ?? string.Empty), id, cancellationToken);
        group.Update(request.Name!, request.Description);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var group = await FindAsync(id, cancellationToken);
        var tokens = await db.Set<EnrollmentToken>().Where(t => t.DeviceGroupId == id).ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.DetachGroup();
        }

        await db.SaveChangesAsync(cancellationToken);

        // Memberships cascade in the database.
        db.Set<DeviceGroup>().Remove(group);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DeviceGroupDto> AddDevicesAsync(Guid id, AddGroupDevicesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var group = await FindAsync(id, cancellationToken);
        var requested = (request.DeviceIds ?? []).Distinct().ToList();
        var found = await db.Set<Device>().Where(d => requested.Contains(d.Id)).Select(d => d.Id).ToListAsync(cancellationToken);
        var missing = requested.Except(found).ToList();
        if (missing.Count > 0)
        {
            throw new NotFoundException("Device", missing[0]);
        }

        var already = await db.Set<DeviceGroupMembership>()
            .Where(m => m.DeviceGroupId == id && requested.Contains(m.DeviceId))
            .Select(m => m.DeviceId)
            .ToListAsync(cancellationToken);
        foreach (var deviceId in requested.Except(already))
        {
            db.Set<DeviceGroupMembership>().Add(DeviceGroupMembership.Create(group.TenantId, id, deviceId));
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeviceGroupDto> RemoveDeviceAsync(Guid id, Guid deviceId, CancellationToken cancellationToken)
    {
        await FindAsync(id, cancellationToken);
        var membership = await db.Set<DeviceGroupMembership>().FirstOrDefaultAsync(m => m.DeviceGroupId == id && m.DeviceId == deviceId, cancellationToken)
            ?? throw new NotFoundException("Device", deviceId);
        db.Set<DeviceGroupMembership>().Remove(membership);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    private IQueryable<DeviceGroupDto> Project(IQueryable<DeviceGroup> groups) =>
        groups.Select(g => new DeviceGroupDto(
            g.Id,
            g.Name,
            g.Description,
            db.Set<DeviceGroupMembership>().Count(m => m.DeviceGroupId == g.Id),
            g.CreatedAtUtc,
            g.UpdatedAtUtc));

    private async Task<DeviceGroup> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<DeviceGroup>().FirstOrDefaultAsync(g => g.Id == id, cancellationToken)
        ?? throw new NotFoundException("Device group", id);

    private async Task EnsureUniqueNameAsync(string normalizedName, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (await db.Set<DeviceGroup>().AnyAsync(g => g.NormalizedName == normalizedName && g.Id != exceptId, cancellationToken))
        {
            throw new ConflictException("A device group with this name already exists.");
        }
    }
}
