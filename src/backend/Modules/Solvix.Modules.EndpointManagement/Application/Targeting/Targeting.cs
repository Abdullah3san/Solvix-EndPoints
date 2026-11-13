namespace Solvix.Modules.EndpointManagement.Application.Targeting;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.EndpointManagement.Domain.Targeting;
using Solvix.Modules.Organization.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;

public sealed record DeviceTargetInfo(Guid Id, string Hostname, Guid? BranchId, Guid? DepartmentId, IReadOnlySet<Guid> GroupIds);

/// <summary>
/// An in-memory picture of the tenant's active devices, group memberships and
/// department tree, loaded once and used to resolve any number of targets.
/// Pure and deterministic, so the targeting rules are unit-testable.
/// </summary>
public sealed class TargetingSnapshot
{
    private readonly Dictionary<Guid, Guid?> _departmentParents;

    public TargetingSnapshot(IReadOnlyList<DeviceTargetInfo> devices, IReadOnlyDictionary<Guid, Guid?> departmentParents)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(departmentParents);
        Devices = devices;
        _departmentParents = new Dictionary<Guid, Guid?>(departmentParents);
        ById = devices.ToDictionary(d => d.Id);
    }

    public IReadOnlyList<DeviceTargetInfo> Devices { get; }

    public IReadOnlyDictionary<Guid, DeviceTargetInfo> ById { get; }

    public bool Matches(DeviceTargetInfo device, TargetRef target)
    {
        ArgumentNullException.ThrowIfNull(device);
        return target.Type switch
        {
            TargetType.AllDevices => true,
            TargetType.Device => device.Id == target.Id,
            TargetType.Branch => device.BranchId is not null && device.BranchId == target.Id,
            TargetType.Department => device.DepartmentId is { } dept && target.Id is { } wanted && IsSameOrDescendant(dept, wanted),
            TargetType.DeviceGroup => target.Id is { } group && device.GroupIds.Contains(group),
            _ => false,
        };
    }

    /// <summary>Devices matched by at least one inclusion and by no exclusion.</summary>
    public IReadOnlyList<DeviceTargetInfo> Resolve(IEnumerable<TargetRef> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var list = targets.ToList();
        var includes = list.Where(t => !t.IsExclusion).ToList();
        var excludes = list.Where(t => t.IsExclusion).ToList();
        return Devices
            .Where(d => includes.Any(t => Matches(d, t)) && !excludes.Any(t => Matches(d, t)))
            .ToList();
    }

    /// <summary>Walks up the department tree (depth-bounded, cycle-safe).</summary>
    public bool IsSameOrDescendant(Guid departmentId, Guid ancestorId)
    {
        Guid? current = departmentId;
        for (var depth = 0; current is { } id && depth < 64; depth++)
        {
            if (id == ancestorId)
            {
                return true;
            }

            current = _departmentParents.GetValueOrDefault(id);
        }

        return false;
    }
}

public interface ITargetingService
{
    /// <summary>Loads active devices (optionally just one) plus groups and the department tree.</summary>
    Task<TargetingSnapshot> LoadAsync(CancellationToken cancellationToken, Guid? onlyDeviceId = null);

    /// <summary>Rejects targets that do not exist in the current tenant (foreign ids look exactly like missing ones).</summary>
    Task ValidateAsync(IReadOnlyList<TargetRef> targets, string propertyName, CancellationToken cancellationToken);
}

internal sealed class TargetingService(ISolvixDbContext db) : ITargetingService
{
    public async Task<TargetingSnapshot> LoadAsync(CancellationToken cancellationToken, Guid? onlyDeviceId = null)
    {
        var devicesQuery = db.Set<Device>().AsNoTracking().Where(d => d.Status == DeviceStatus.Active);
        var membersQuery = db.Set<DeviceGroupMember>().AsNoTracking();
        if (onlyDeviceId is { } id)
        {
            devicesQuery = devicesQuery.Where(d => d.Id == id);
            membersQuery = membersQuery.Where(m => m.DeviceId == id);
        }

        var devices = await devicesQuery
            .Select(d => new { d.Id, d.Hostname, d.BranchId, d.DepartmentId })
            .ToListAsync(cancellationToken);
        var members = await membersQuery.Select(m => new { m.DeviceId, m.DeviceGroupId }).ToListAsync(cancellationToken);
        var groupsByDevice = members.GroupBy(m => m.DeviceId).ToDictionary(g => g.Key, g => g.Select(m => m.DeviceGroupId).ToHashSet());
        var departments = await db.Set<Department>().AsNoTracking()
            .Select(d => new { d.Id, d.ParentDepartmentId })
            .ToDictionaryAsync(d => d.Id, d => d.ParentDepartmentId, cancellationToken);

        return new TargetingSnapshot(
            devices.Select(d => new DeviceTargetInfo(d.Id, d.Hostname, d.BranchId, d.DepartmentId, groupsByDevice.GetValueOrDefault(d.Id) ?? [])).ToList(),
            departments);
    }

    public async Task ValidateAsync(IReadOnlyList<TargetRef> targets, string propertyName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (t.Type == TargetType.AllDevices)
            {
                continue;
            }

            if (t.Id is not { } id)
            {
                errors[$"{propertyName}[{i}].targetId"] = [$"targetId is required for {t.Type} targets."];
                continue;
            }

            var exists = t.Type switch
            {
                TargetType.Branch => await db.Set<Branch>().AnyAsync(b => b.Id == id, cancellationToken),
                TargetType.Department => await db.Set<Department>().AnyAsync(d => d.Id == id, cancellationToken),
                TargetType.DeviceGroup => await db.Set<DeviceGroup>().AnyAsync(g => g.Id == id, cancellationToken),
                TargetType.Device => await db.Set<Device>().AnyAsync(d => d.Id == id, cancellationToken),
                _ => false,
            };
            if (!exists)
            {
                errors[$"{propertyName}[{i}].targetId"] = [$"{t.Type} '{id}' was not found."];
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
