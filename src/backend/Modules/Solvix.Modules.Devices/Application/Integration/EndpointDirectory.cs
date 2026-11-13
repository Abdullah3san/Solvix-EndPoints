namespace Solvix.Modules.Devices.Application.Integration;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Monitoring;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Persistence;

/// <summary>Exposes enrolled devices to other modules (asset management) through <see cref="IEndpointDirectory"/>.</summary>
internal sealed class EndpointDirectory(ISolvixDbContext db) : IEndpointDirectory
{
    public const int MaxBatch = 5000;

    public async Task<IReadOnlyDictionary<Guid, EndpointSummary>> FindAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (deviceIds.Count == 0)
        {
            return new Dictionary<Guid, EndpointSummary>();
        }

        var ids = deviceIds.Distinct().ToList();
        var rows = await Project(db.Set<Device>().AsNoTracking().Where(d => ids.Contains(d.Id))).ToListAsync(cancellationToken);
        return rows.Select(ToSummary).ToDictionary(r => r.Id);
    }

    public async Task<EndpointAccessInfo?> FindForAccessAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var row = await Project(db.Set<Device>().AsNoTracking().Where(d => d.Id == deviceId)).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var groups = await db.Set<DeviceGroupMembership>().AsNoTracking()
            .Where(m => m.DeviceId == deviceId)
            .Select(m => m.DeviceGroupId)
            .ToListAsync(cancellationToken);
        return new EndpointAccessInfo(ToSummary(row), groups);
    }

    public async Task<IReadOnlyList<EndpointSummary>> FindBySerialKeysAsync(IReadOnlyCollection<string> serialKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serialKeys);
        var keys = serialKeys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).Take(MaxBatch).ToList();
        if (keys.Count == 0)
        {
            return [];
        }

        var rows = await Project(db.Set<Device>().AsNoTracking().Where(d => d.SerialNumberKey != null && keys.Contains(d.SerialNumberKey)))
            .ToListAsync(cancellationToken);
        return rows.Select(ToSummary).ToList();
    }

    public async Task<EndpointPage> SearchAsync(
        string? search, IReadOnlyCollection<Guid> excludedDeviceIds, bool activeOnly, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(excludedDeviceIds);
        var devices = db.Set<Device>().AsNoTracking();
        if (activeOnly)
        {
            devices = devices.Where(d => d.Status == DeviceStatus.Active);
        }

        if (excludedDeviceIds.Count > 0)
        {
            var excluded = excludedDeviceIds.Distinct().ToList();
            devices = devices.Where(d => !excluded.Contains(d.Id));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            devices = devices.Where(d =>
                EF.Functions.ILike(d.Hostname, pattern)
                || (d.DisplayName != null && EF.Functions.ILike(d.DisplayName, pattern))
                || (d.SerialNumber != null && EF.Functions.ILike(d.SerialNumber, pattern))
                || (d.Model != null && EF.Functions.ILike(d.Model, pattern)));
        }

        var total = await devices.LongCountAsync(cancellationToken);
        var items = await Project(devices.OrderBy(d => d.Hostname).ThenBy(d => d.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)))
            .ToListAsync(cancellationToken);
        return new EndpointPage(items.Select(ToSummary).ToList(), total);
    }

    public async Task<IReadOnlyList<EndpointAccessInfo>> ResolveAsync(
        IReadOnlyCollection<Guid> deviceIds, IReadOnlyCollection<Guid> groupIds, IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        ArgumentNullException.ThrowIfNull(groupIds);
        ArgumentNullException.ThrowIfNull(branchIds);
        var ids = deviceIds.Distinct().ToList();
        var groups = groupIds.Distinct().ToList();
        var branches = branchIds.Distinct().ToList();
        if (ids.Count == 0 && groups.Count == 0 && branches.Count == 0)
        {
            return [];
        }

        var memberIds = db.Set<DeviceGroupMembership>().Where(m => groups.Contains(m.DeviceGroupId)).Select(m => m.DeviceId);
        var devices = db.Set<Device>().AsNoTracking().Where(d =>
            ids.Contains(d.Id) || memberIds.Contains(d.Id) || (d.BranchId != null && branches.Contains(d.BranchId.Value)));
        var rows = await Project(devices.OrderBy(d => d.Hostname).ThenBy(d => d.Id).Take(MaxBatch)).ToListAsync(cancellationToken);
        var found = rows.Select(r => r.Device.Id).ToList();
        var memberships = await db.Set<DeviceGroupMembership>().AsNoTracking()
            .Where(m => found.Contains(m.DeviceId))
            .Select(m => new { m.DeviceId, m.DeviceGroupId })
            .ToListAsync(cancellationToken);
        var byDevice = memberships.ToLookup(m => m.DeviceId, m => m.DeviceGroupId);
        return rows.Select(r => new EndpointAccessInfo(ToSummary(r), byDevice[r.Device.Id].ToList())).ToList();
    }

    public Task<int> CountActiveAsync(CancellationToken cancellationToken) =>
        db.Set<Device>().CountAsync(d => d.Status == DeviceStatus.Active, cancellationToken);

    private IQueryable<Row> Project(IQueryable<Device> devices) =>
        from d in devices
        join p in db.Set<DevicePresence>().AsNoTracking() on d.Id equals p.Id into presence
        from p in presence.DefaultIfEmpty()
        select new Row(d, p == null ? null : p.State, p == null ? null : p.LastSeenAtUtc);

    private static EndpointSummary ToSummary(Row row)
    {
        var d = row.Device;
        var active = d.Status == DeviceStatus.Active;
        return new EndpointSummary(
            d.Id,
            d.Hostname,
            d.DisplayName,
            active,
            d.SerialNumber,
            d.Manufacturer,
            d.Model,
            d.OsName,
            (active ? row.State ?? ConnectivityState.Offline : ConnectivityState.Offline).ToString(),
            row.LastSeenAtUtc,
            d.EnrolledAtUtc,
            d.AssignedUserId,
            d.BranchId,
            d.DepartmentId);
    }

    private sealed record Row(Device Device, ConnectivityState? State, DateTimeOffset? LastSeenAtUtc);
}

/// <summary>Publishes hardware identity changes to <see cref="IEndpointIdentityObserver"/>s without ever failing the caller.</summary>
internal sealed partial class EndpointIdentityPublisher(
    IEnumerable<IEndpointIdentityObserver> observers,
    Microsoft.Extensions.Logging.ILogger<EndpointIdentityPublisher> logger)
{
    public async Task PublishAsync(Guid deviceId, EndpointIdentityChangeKind kind, CancellationToken cancellationToken)
    {
        foreach (var observer in observers)
        {
            try
            {
                await observer.OnEndpointIdentityChangedAsync(deviceId, kind, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogObserverFailed(logger, ex, observer.GetType().Name, deviceId, kind);
            }
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Endpoint identity observer {Observer} failed for device {DeviceId} ({Kind})")]
    private static partial void LogObserverFailed(Microsoft.Extensions.Logging.ILogger logger, Exception exception, string observer, Guid deviceId, EndpointIdentityChangeKind kind);
}
