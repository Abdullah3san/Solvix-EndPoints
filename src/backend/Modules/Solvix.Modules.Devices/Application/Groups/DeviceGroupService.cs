namespace Solvix.Modules.Devices.Application.Groups;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IDeviceGroupService
{
    Task<IReadOnlyList<DeviceGroupDto>> ListAsync(CancellationToken cancellationToken);

    Task<DeviceGroupDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DeviceGroupDto> CreateAsync(SaveDeviceGroupRequest request, CancellationToken cancellationToken);

    Task<DeviceGroupDto> UpdateAsync(Guid id, SaveDeviceGroupRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task<DeviceGroupDto> AddMembersAsync(Guid id, DeviceGroupMembersRequest request, CancellationToken cancellationToken);

    Task<DeviceGroupDto> RemoveMembersAsync(Guid id, DeviceGroupMembersRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Lets other modules veto deleting a group that is still used for targeting
/// (deployment pilot rings, policy assignments). Implemented by EndpointManagement.
/// </summary>
public interface IDeviceGroupUsageCheck
{
    Task<string?> DescribeUsageAsync(Guid deviceGroupId, CancellationToken cancellationToken);
}

internal sealed class DeviceGroupService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    IAuditTrail audit,
    IEnumerable<IDeviceGroupUsageCheck> usageChecks) : IDeviceGroupService
{
    public async Task<IReadOnlyList<DeviceGroupDto>> ListAsync(CancellationToken cancellationToken) =>
        await db.Set<DeviceGroup>().AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new DeviceGroupDto(g.Id, g.Name, g.Description, db.Set<DeviceGroupMember>().Count(m => m.DeviceGroupId == g.Id), g.CreatedAtUtc))
            .ToListAsync(cancellationToken);

    public async Task<DeviceGroupDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<DeviceGroup>().AsNoTracking()
            .Where(g => g.Id == id)
            .Select(g => new DeviceGroupDto(g.Id, g.Name, g.Description, db.Set<DeviceGroupMember>().Count(m => m.DeviceGroupId == g.Id), g.CreatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException("DeviceGroup", id);

    public async Task<DeviceGroupDto> CreateAsync(SaveDeviceGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureUniqueNameAsync(request.Name, null, cancellationToken);
        var group = DeviceGroup.Create(tenantContext.RequireTenantId(), request.Name, request.Description);
        db.Set<DeviceGroup>().Add(group);
        audit.Record("device_group.created", nameof(DeviceGroup), group.Id, $"Device group '{group.Name}' created.");
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(group.Id, cancellationToken);
    }

    public async Task<DeviceGroupDto> UpdateAsync(Guid id, SaveDeviceGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var group = await FindAsync(id, cancellationToken);
        await EnsureUniqueNameAsync(request.Name, id, cancellationToken);
        group.Update(request.Name, request.Description);
        audit.Record("device_group.updated", nameof(DeviceGroup), group.Id, $"Device group '{group.Name}' updated.");
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var group = await FindAsync(id, cancellationToken);
        foreach (var check in usageChecks)
        {
            if (await check.DescribeUsageAsync(id, cancellationToken) is { } usage)
            {
                throw new ConflictException($"The device group is still in use: {usage}");
            }
        }

        db.Set<DeviceGroup>().Remove(group);
        audit.Record("device_group.deleted", nameof(DeviceGroup), group.Id, $"Device group '{group.Name}' deleted.");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DeviceGroupDto> AddMembersAsync(Guid id, DeviceGroupMembersRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var group = await FindAsync(id, cancellationToken);
        var ids = request.DeviceIds.Distinct().ToList();
        var known = await db.Set<Device>().Where(d => ids.Contains(d.Id)).Select(d => d.Id).ToListAsync(cancellationToken);
        var missing = ids.Except(known).ToList();
        if (missing.Count > 0)
        {
            // Unknown and other tenants' devices are indistinguishable on purpose.
            throw new ValidationException("deviceIds", $"Unknown device id(s): {string.Join(", ", missing)}.");
        }

        var existing = await db.Set<DeviceGroupMember>()
            .Where(m => m.DeviceGroupId == id && ids.Contains(m.DeviceId))
            .Select(m => m.DeviceId)
            .ToListAsync(cancellationToken);
        var tenantId = tenantContext.RequireTenantId();
        var added = ids.Except(existing).ToList();
        foreach (var deviceId in added)
        {
            db.Set<DeviceGroupMember>().Add(new DeviceGroupMember(tenantId, id, deviceId));
        }

        if (added.Count > 0)
        {
            audit.Record("device_group.members_added", nameof(DeviceGroup), id, $"{added.Count} device(s) added to '{group.Name}'.", new { deviceIds = added });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeviceGroupDto> RemoveMembersAsync(Guid id, DeviceGroupMembersRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var group = await FindAsync(id, cancellationToken);
        var ids = request.DeviceIds.Distinct().ToList();
        var members = await db.Set<DeviceGroupMember>().Where(m => m.DeviceGroupId == id && ids.Contains(m.DeviceId)).ToListAsync(cancellationToken);
        if (members.Count > 0)
        {
            db.Set<DeviceGroupMember>().RemoveRange(members);
            audit.Record("device_group.members_removed", nameof(DeviceGroup), id, $"{members.Count} device(s) removed from '{group.Name}'.", new { deviceIds = members.Select(m => m.DeviceId) });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(id, cancellationToken);
    }

    private async Task<DeviceGroup> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<DeviceGroup>().FirstOrDefaultAsync(g => g.Id == id, cancellationToken)
        ?? throw new NotFoundException("DeviceGroup", id);

    private async Task EnsureUniqueNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        if (await db.Set<DeviceGroup>().AnyAsync(g => g.Name == trimmed && g.Id != exceptId, cancellationToken))
        {
            throw new ConflictException($"A device group named '{trimmed}' already exists.");
        }
    }
}
