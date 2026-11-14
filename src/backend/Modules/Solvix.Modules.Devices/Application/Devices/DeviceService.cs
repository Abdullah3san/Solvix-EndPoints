namespace Solvix.Modules.Devices.Application.Devices;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application.Agent;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public interface IDeviceService
{
    Task<PagedResult<DeviceSummaryDto>> ListAsync(DeviceListQuery query, CancellationToken cancellationToken);

    Task<DeviceStatsDto> GetStatsAsync(CancellationToken cancellationToken);

    Task<DeviceDetailsDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<DeviceInventoryDto> GetInventoryAsync(Guid id, CancellationToken cancellationToken);

    Task<DeviceDetailsDto> UpdateAsync(Guid id, UpdateDeviceRequest request, CancellationToken cancellationToken);

    Task<DeviceDetailsDto> AssignAsync(Guid id, UpdateDeviceAssignmentRequest request, CancellationToken cancellationToken);

    Task<DeviceDetailsDto> SetGroupsAsync(Guid id, SetDeviceGroupsRequest request, CancellationToken cancellationToken);

    Task<DeviceDetailsDto> RequestInventoryAsync(Guid id, CancellationToken cancellationToken);

    Task<DeviceDetailsDto> RetireAsync(Guid id, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class DeviceService(
    ISolvixDbContext db,
    IMemberDirectory members,
    IOrganizationDirectory organization,
    IAgentReleaseCatalog releases,
    IOptions<DevicesOptions> options,
    IClock clock) : IDeviceService
{
    public async Task<PagedResult<DeviceSummaryDto>> ListAsync(DeviceListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var now = clock.UtcNow;
        var onlineSince = OnlineSince(now);
        var devices = db.Set<Device>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var pattern = $"%{term}%";
            devices = devices.Where(d =>
                EF.Functions.ILike(d.Hostname, pattern)
                || (d.DisplayName != null && EF.Functions.ILike(d.DisplayName, pattern))
                || (d.SerialNumber != null && EF.Functions.ILike(d.SerialNumber, pattern))
                || (d.Model != null && EF.Functions.ILike(d.Model, pattern))
                || d.IpAddresses.Contains(term)
                || d.MacAddresses.Contains(term.ToUpperInvariant()));
        }

        if (query.Status is { } status)
        {
            devices = devices.Where(d => d.Status == status);
        }

        devices = query.Connectivity switch
        {
            DeviceConnectivity.Online => devices.Where(d => d.Status == DeviceStatus.Active && d.LastSeenAtUtc >= onlineSince),
            DeviceConnectivity.Offline => devices.Where(d => d.Status == DeviceStatus.Active && (d.LastSeenAtUtc == null || d.LastSeenAtUtc < onlineSince)),
            _ => devices,
        };

        if (query.GroupId is { } groupId)
        {
            devices = devices.Where(d => db.Set<DeviceGroupMembership>().Any(m => m.DeviceId == d.Id && m.DeviceGroupId == groupId));
        }

        if (!string.IsNullOrWhiteSpace(query.AgentVersion))
        {
            var version = query.AgentVersion.Trim();
            devices = devices.Where(d => d.AgentVersion == version);
        }

        if (query.AssignedUserId is { } userId)
        {
            devices = devices.Where(d => d.AssignedUserId == userId);
        }

        var total = await devices.LongCountAsync(cancellationToken);
        var items = await devices
            .OrderBy(d => d.Hostname).ThenBy(d => d.Id)
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<DeviceSummaryDto>(items.Select(d => ToSummary(d, now)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<DeviceStatsDto> GetStatsAsync(CancellationToken cancellationToken)
    {
        var onlineSince = OnlineSince(clock.UtcNow);
        var devices = db.Set<Device>().AsNoTracking();
        var total = await devices.CountAsync(cancellationToken);
        var retired = await devices.CountAsync(d => d.Status == DeviceStatus.Retired, cancellationToken);
        var online = await devices.CountAsync(d => d.Status == DeviceStatus.Active && d.LastSeenAtUtc >= onlineSince, cancellationToken);
        var versions = await devices
            .Where(d => d.Status == DeviceStatus.Active)
            .GroupBy(d => d.AgentVersion)
            .Select(g => new { Version = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var latest = releases.LatestVersion;
        var versionCounts = versions
            .Select(v => new AgentVersionCountDto(
                v.Version,
                v.Count,
                latest is not null && AgentVersion.TryParse(v.Version, out var parsed) && parsed == AgentVersion.Parse(latest),
                releases.IsUpdateAvailable(v.Version)))
            .OrderByDescending(v => AgentVersion.TryParse(v.Version, out var parsed) ? parsed : null)
            .ToList();

        var active = total - retired;
        return new DeviceStatsDto(
            total,
            active,
            online,
            active - online,
            retired,
            versionCounts.Where(v => v.UpdateAvailable).Sum(v => v.DeviceCount),
            latest,
            versionCounts);
    }

    public async Task<DeviceDetailsDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await ToDetailsAsync(await FindAsync(id, tracking: false, cancellationToken), cancellationToken);

    public async Task<DeviceInventoryDto> GetInventoryAsync(Guid id, CancellationToken cancellationToken)
    {
        await FindAsync(id, tracking: false, cancellationToken);
        var inventory = await db.Set<DeviceInventory>().AsNoTracking().FirstOrDefaultAsync(i => i.DeviceId == id, cancellationToken)
            ?? throw new NotFoundException("No inventory has been reported for this device yet.");
        var report = JsonSerializer.Deserialize<InventoryReport>(inventory.ReportJson, DevicesJson.Options)
            ?? throw new InvalidOperationException("Stored inventory could not be read.");
        return new DeviceInventoryDto(id, inventory.CollectedAtUtc, inventory.ReceivedAtUtc, inventory.ContentHash, report);
    }

    public async Task<DeviceDetailsDto> UpdateAsync(Guid id, UpdateDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await FindAsync(id, tracking: true, cancellationToken);
        device.UpdateDetails(request.DisplayName, request.Notes);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(device, cancellationToken);
    }

    public async Task<DeviceDetailsDto> AssignAsync(Guid id, UpdateDeviceAssignmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await FindAsync(id, tracking: true, cancellationToken);

        if (request.AssignedUserId is { } userId)
        {
            var found = await members.FindAsync([userId], cancellationToken);
            if (!found.TryGetValue(userId, out var member))
            {
                throw new NotFoundException("Member", userId);
            }

            if (!member.IsActive)
            {
                throw new ValidationException("assignedUserId", "The user is not an active member of this workspace.");
            }
        }

        var branchId = request.BranchId;
        if (request.DepartmentId is { } departmentId)
        {
            var departments = await organization.FindDepartmentsAsync([departmentId], cancellationToken);
            if (!departments.TryGetValue(departmentId, out var department))
            {
                throw new NotFoundException("Department", departmentId);
            }

            if (branchId is not null && branchId != department.BranchId)
            {
                throw new ValidationException("departmentId", "The department does not belong to the selected branch.");
            }

            branchId = department.BranchId;
        }

        if (branchId is { } b && !(await organization.FindBranchesAsync([b], cancellationToken)).ContainsKey(b))
        {
            throw new NotFoundException("Branch", b);
        }

        device.Assign(request.AssignedUserId, branchId, request.DepartmentId);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(device, cancellationToken);
    }

    public async Task<DeviceDetailsDto> SetGroupsAsync(Guid id, SetDeviceGroupsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await FindAsync(id, tracking: false, cancellationToken);
        var wanted = (request.GroupIds ?? []).Distinct().ToList();
        var existingGroups = await db.Set<DeviceGroup>().Where(g => wanted.Contains(g.Id)).Select(g => g.Id).ToListAsync(cancellationToken);
        var missing = wanted.Except(existingGroups).ToList();
        if (missing.Count > 0)
        {
            throw new NotFoundException("Device group", missing[0]);
        }

        var memberships = await db.Set<DeviceGroupMembership>().Where(m => m.DeviceId == id).ToListAsync(cancellationToken);
        db.Set<DeviceGroupMembership>().RemoveRange(memberships.Where(m => !wanted.Contains(m.DeviceGroupId)));
        foreach (var groupId in wanted.Except(memberships.Select(m => m.DeviceGroupId)))
        {
            db.Set<DeviceGroupMembership>().Add(DeviceGroupMembership.Create(device.TenantId, groupId, id));
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(device, cancellationToken);
    }

    public async Task<DeviceDetailsDto> RequestInventoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await FindAsync(id, tracking: true, cancellationToken);
        device.RequestInventory(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(device, cancellationToken);
    }

    public async Task<DeviceDetailsDto> RetireAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await FindAsync(id, tracking: true, cancellationToken);
        var now = clock.UtcNow;
        device.Retire(now);
        var credentials = await db.Set<DeviceCredential>().Where(c => c.DeviceId == id && c.RevokedAtUtc == null).ToListAsync(cancellationToken);
        foreach (var credential in credentials)
        {
            credential.Revoke(CredentialRevocationReason.DeviceRetired, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailsAsync(device, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await FindAsync(id, tracking: true, cancellationToken);

        // Credentials, inventory and memberships cascade in the database.
        db.Set<Device>().Remove(device);
        await db.SaveChangesAsync(cancellationToken);
    }

    private DateTimeOffset OnlineSince(DateTimeOffset now) => now.AddSeconds(-options.Value.Agent.OfflineAfterSeconds);

    private async Task<Device> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.Set<Device>() : db.Set<Device>().AsNoTracking();
        return await query.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new NotFoundException("Device", id);
    }

    private DeviceSummaryDto ToSummary(Device d, DateTimeOffset now) =>
        new(
            d.Id,
            d.Hostname,
            d.DisplayName,
            d.Platform,
            d.Status,
            d.Status == DeviceStatus.Active && d.LastSeenAtUtc >= OnlineSince(now) ? DeviceConnectivity.Online : DeviceConnectivity.Offline,
            d.OsName,
            d.OsVersion,
            d.OsDisplayVersion,
            d.OsBuild,
            d.Manufacturer,
            d.Model,
            d.SerialNumber,
            d.AgentVersion,
            releases.IsUpdateAvailable(d.AgentVersion),
            d.HealthStatus,
            d.IpAddresses,
            d.AssignedUserId,
            d.LastSeenAtUtc,
            d.LastInventoryAtUtc,
            d.EnrolledAtUtc);

    private async Task<DeviceDetailsDto> ToDetailsAsync(Device d, CancellationToken cancellationToken)
    {
        var deviceId = d.Id;
        var groups = (await db.Set<DeviceGroup>().AsNoTracking()
                .Where(g => db.Set<DeviceGroupMembership>().Any(m => m.DeviceId == deviceId && m.DeviceGroupId == g.Id))
                .OrderBy(g => g.Name)
                .Select(g => new { g.Id, g.Name })
                .ToListAsync(cancellationToken))
            .Select(g => new DeviceGroupRefDto(g.Id, g.Name))
            .ToList();
        var credentials = await db.Set<DeviceCredential>().AsNoTracking()
            .Where(c => c.DeviceId == d.Id)
            .OrderByDescending(c => c.CreatedAtUtc)
            .Take(20)
            .ToListAsync(cancellationToken);

        AssignedUserDto? user = null;
        if (d.AssignedUserId is { } userId)
        {
            var found = await members.FindAsync([userId], cancellationToken);
            user = found.TryGetValue(userId, out var m)
                ? new AssignedUserDto(userId, m.DisplayName, m.Email, m.IsActive)
                : new AssignedUserDto(userId, null, null, false);
        }

        AssignedUnitDto? branch = null;
        if (d.BranchId is { } branchId)
        {
            var found = await organization.FindBranchesAsync([branchId], cancellationToken);
            branch = found.TryGetValue(branchId, out var b) ? new AssignedUnitDto(branchId, b.Name, b.Code) : new AssignedUnitDto(branchId, null, null);
        }

        AssignedUnitDto? department = null;
        if (d.DepartmentId is { } departmentId)
        {
            var found = await organization.FindDepartmentsAsync([departmentId], cancellationToken);
            department = found.TryGetValue(departmentId, out var dep) ? new AssignedUnitDto(departmentId, dep.Name, dep.Code) : new AssignedUnitDto(departmentId, null, null);
        }

        return new DeviceDetailsDto
        {
            Summary = ToSummary(d, clock.UtcNow),
            Notes = d.Notes,
            HardwareId = d.HardwareId,
            DomainName = d.DomainName,
            Architecture = d.Architecture,
            ProcessorName = d.ProcessorName,
            TotalMemoryBytes = d.TotalMemoryBytes,
            LastBootAtUtc = d.LastBootAtUtc,
            MacAddresses = d.MacAddresses,
            AgentVersionChangedAtUtc = d.AgentVersionChangedAtUtc,
            Health = d.AgentHealthJson is null ? null : JsonSerializer.Deserialize<AgentHealthReport>(d.AgentHealthJson, DevicesJson.Options),
            Groups = groups,
            Assignment = new DeviceAssignmentDto(user, branch, department),
            Credentials = credentials.Select(DeviceCredentialDto.From).ToList(),
            LastEnrolledAtUtc = d.LastEnrolledAtUtc,
            EnrollmentCount = d.EnrollmentCount,
            InventoryRequested = d.IsInventoryRequested,
            RetiredAtUtc = d.RetiredAtUtc,
            CreatedAtUtc = d.CreatedAtUtc,
            UpdatedAtUtc = d.UpdatedAtUtc,
        };
    }
}
