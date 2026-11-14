namespace Solvix.Infrastructure.Fleet;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Solvix.Agent.Contracts;
using Solvix.Infrastructure.Persistence;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Monitoring;
using Solvix.Modules.Organization.Domain;
using Solvix.Modules.Software.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Fleet;

/// <summary>
/// Read-only fleet data for the AI assistant (the <see cref="IFleetQueryService"/> port), built from
/// what the platform actually stores: devices (Phase 03), presence and OS health (Phase 04), inventory
/// reports and installed software (Phases 03/07) and the package repository (Phase 07).
///
/// Every call checks the caller's own permissions (<c>devices.view</c>, plus <c>software.view</c> for
/// software data) and filters by the caller's tenant on top of Row-Level Security.
///
/// Nothing is invented: data the agents do not collect (CPU history, temperatures, battery, antivirus,
/// firewall, patch level) is reported as unavailable rather than estimated.
/// </summary>
internal sealed class PlatformFleetQueryService(SolvixDbContext db, IClock clock) : IFleetQueryService
{
    private const long LowStorageBytes = 10L * 1024 * 1024 * 1024;
    private static readonly TimeSpan StaleAgentAfter = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public bool IsAvailable => true;

    public async Task<FleetSummary> GetSummaryAsync(FleetAccess access, FleetScope scope, CancellationToken cancellationToken)
    {
        var rows = InScope(await LoadAsync(access, null, cancellationToken), scope);
        return new FleetSummary(
            rows.Count,
            rows.Count(r => r.Summary.Status == DeviceConnectivity.Online),
            rows.Count(r => r.Summary.Status == DeviceConnectivity.Offline),
            rows.Count(r => r.Summary.Status == DeviceConnectivity.Unknown),
            rows.Count(r => r.Summary.FreeStorageBytes is < LowStorageBytes),
            rows.GroupBy(r => r.Summary.OperatingSystem ?? "Unknown").ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            [.. rows.GroupBy(r => r.Summary.BranchName ?? "No branch")
                .Select(g => new BranchFleetCount(
                    g.Key,
                    g.Count(),
                    g.Count(r => r.Summary.Status == DeviceConnectivity.Online),
                    g.Count(r => r.Summary.Status == DeviceConnectivity.Offline)))
                .OrderBy(b => b.BranchName, StringComparer.OrdinalIgnoreCase)]);
    }

    public async Task<(IReadOnlyList<DeviceSummary> Items, long Total)> SearchAsync(FleetAccess access, DeviceSearchCriteria criteria, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        IEnumerable<Row> rows = await LoadAsync(access, null, cancellationToken);
        var now = clock.UtcNow;

        if (!string.IsNullOrWhiteSpace(criteria.Text))
        {
            var text = criteria.Text.Trim();
            rows = rows.Where(r => Contains(r.Summary.Name, text) || Contains(r.Device.Hostname, text) || Contains(r.Device.SerialNumber, text)
                || Contains(r.Summary.Model, text) || Contains(r.Summary.Manufacturer, text));
        }

        if (criteria.Status is { } status)
        {
            rows = rows.Where(r => r.Summary.Status == status);
        }

        rows = InScope([.. rows], new FleetScope(criteria.BranchId, criteria.DepartmentIds));

        if (criteria.MaxFreeStorageBytes is { } maxFree)
        {
            rows = rows.Where(r => r.Summary.FreeStorageBytes <= maxFree);
        }

        if (criteria.MinCpuUsagePercent is { } minCpu)
        {
            rows = rows.Where(r => r.Summary.CpuUsagePercent >= minCpu);
        }

        if (criteria.MinMemoryUsagePercent is { } minMemory)
        {
            rows = rows.Where(r => r.Summary.MemoryUsagePercent >= minMemory);
        }

        if (!string.IsNullOrWhiteSpace(criteria.OperatingSystem))
        {
            rows = rows.Where(r => Contains(r.Summary.OperatingSystem, criteria.OperatingSystem.Trim()));
        }

        if (criteria.OfflineFor is { } offlineFor)
        {
            rows = rows.Where(r => r.Summary.Status == DeviceConnectivity.Offline && r.Presence?.OfflineSinceUtc is { } since && now - since >= offlineFor);
        }

        if (!string.IsNullOrWhiteSpace(criteria.InstalledSoftware) || criteria.HasOutdatedSoftware is not null)
        {
            Require(access, Permissions.Software.View);
            var materialized = rows.ToList();
            var software = await SoftwareByDeviceAsync(access, [.. materialized.Select(r => r.Device.Id)], cancellationToken);
            var latest = await LatestRepositoryVersionsAsync(access, cancellationToken);
            if (!string.IsNullOrWhiteSpace(criteria.InstalledSoftware))
            {
                var name = criteria.InstalledSoftware.Trim();
                materialized = [.. materialized.Where(r => software.TryGetValue(r.Device.Id, out var apps) && apps.Any(a => Contains(a.Name, name)))];
            }

            if (criteria.HasOutdatedSoftware is { } outdated)
            {
                materialized = [.. materialized.Where(r =>
                    (software.TryGetValue(r.Device.Id, out var apps) && apps.Any(a => IsOutdated(a.Version, Latest(latest, a.NameKey)))) == outdated)];
            }

            rows = materialized;
        }

        rows = criteria.Sort switch
        {
            DeviceSort.LastSeen => rows.OrderByDescending(r => r.Summary.LastSeenUtc ?? DateTimeOffset.MinValue),
            DeviceSort.CpuUsageDesc => rows.OrderByDescending(r => r.Summary.CpuUsagePercent ?? -1),
            DeviceSort.MemoryUsageDesc => rows.OrderByDescending(r => r.Summary.MemoryUsagePercent ?? -1),
            DeviceSort.FreeStorageAsc => rows.OrderBy(r => r.Summary.FreeStorageBytes ?? long.MaxValue),
            _ => rows.OrderBy(r => r.Summary.Name, StringComparer.OrdinalIgnoreCase),
        };

        var all = rows.ToList();
        return ([.. all.Skip(Math.Max(0, skip)).Take(Math.Max(0, take)).Select(r => r.Summary)], all.Count);
    }

    public async Task<DeviceResolution> ResolveAsync(FleetAccess access, IReadOnlyList<string> references, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        var rows = await LoadAsync(access, null, cancellationToken);
        var found = new List<DeviceSummary>();
        var notFound = new List<string>();
        var ambiguous = new List<string>();
        foreach (var raw in references)
        {
            var reference = raw?.Trim() ?? string.Empty;
            var matches = Guid.TryParse(reference, out var id)
                ? rows.Where(r => r.Device.Id == id).ToList()
                : rows.Where(r => string.Equals(r.Summary.Name, reference, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.Device.Hostname, reference, StringComparison.OrdinalIgnoreCase)).ToList();
            switch (matches.Count)
            {
                case 0:
                    notFound.Add(reference);
                    break;
                case 1:
                    if (found.All(d => d.Id != matches[0].Device.Id))
                    {
                        found.Add(matches[0].Summary);
                    }

                    break;
                default:
                    ambiguous.Add(reference);
                    break;
            }
        }

        return new DeviceResolution(found, notFound, ambiguous);
    }

    public async Task<DeviceDetail?> GetDetailAsync(FleetAccess access, Guid deviceId, CancellationToken cancellationToken)
    {
        var row = (await LoadAsync(access, deviceId, cancellationToken)).SingleOrDefault();
        if (row is null)
        {
            return null;
        }

        var inventory = await InventoryAsync(access, deviceId, cancellationToken);
        var disks = inventory?.Volumes
            .Where(v => v.DriveType is null or "Fixed" && v.SizeBytes is > 0)
            .Select(v => new DiskInfo(v.DriveLetter ?? v.Label ?? "Volume", v.FileSystem, v.SizeBytes ?? 0, v.FreeBytes ?? 0, v.BitLockerProtected == true))
            .ToList() ?? [];
        var cores = inventory?.Processors.Sum(p => p.Cores ?? 0);
        return new DeviceDetail(
            row.Summary,
            row.Device.ProcessorName,
            cores is > 0 ? cores : null,
            row.Device.TotalMemoryBytes,
            disks,
            inventory?.Bios?.Version,
            row.Device.SerialNumber,
            row.Device.IpAddresses.FirstOrDefault(),
            string.IsNullOrEmpty(row.Device.AgentVersion) ? null : row.Device.AgentVersion,
            row.Device.LastBootAtUtc ?? row.Presence?.SystemBootedAtUtc,
            row.Health?.RebootPending == true);
    }

    /// <summary>The agents report current values only; no CPU/memory history exists to analyse.</summary>
    public Task<PerformanceReport?> GetPerformanceAsync(FleetAccess access, Guid deviceId, TimeSpan window, CancellationToken cancellationToken)
    {
        Require(access, Permissions.Devices.View);
        return Task.FromResult<PerformanceReport?>(null);
    }

    public async Task<HardwareDiagnostics?> GetHardwareDiagnosticsAsync(FleetAccess access, Guid deviceId, CancellationToken cancellationToken)
    {
        var row = (await LoadAsync(access, deviceId, cancellationToken)).SingleOrDefault();
        var inventory = row is null ? null : await InventoryAsync(access, deviceId, cancellationToken);
        if (row is null || inventory is null)
        {
            return null;
        }

        var bootedAt = row.Device.LastBootAtUtc ?? row.Presence?.SystemBootedAtUtc;
        return new HardwareDiagnostics(
            deviceId,
            row.Summary.Name,
            [.. inventory.Disks.Select(d => new DiskHealth(d.Model ?? "Disk", d.HealthStatus ?? "Unknown", null, null, null))],
            Battery: null,
            MemoryErrorsDetected: false,
            CpuTemperatureCelsius: null,
            UptimeDays: bootedAt is { } boot ? Math.Max(0, (clock.UtcNow - boot).TotalDays) : 0,
            RecentHardwareEvents: []);
    }

    public async Task<IReadOnlyList<InstalledSoftware>?> GetInstalledSoftwareAsync(FleetAccess access, Guid deviceId, string? search, CancellationToken cancellationToken)
    {
        Require(access, Permissions.Software.View);
        if ((await LoadAsync(access, deviceId, cancellationToken)).Count == 0)
        {
            return null;
        }

        var latest = await LatestRepositoryVersionsAsync(access, cancellationToken);
        var query = db.Set<SoftwareInventoryItem>().AsNoTracking().Where(i => i.TenantId == access.TenantId && i.DeviceId == deviceId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var key = SoftwareInventoryItem.KeyOf(search);
            query = query.Where(i => i.NameKey.Contains(key));
        }

        var items = await query.OrderBy(i => i.NameKey).ToListAsync(cancellationToken);
        return [.. items.Select(i =>
        {
            var newest = Latest(latest, i.NameKey);
            return new InstalledSoftware(
                i.Name,
                i.Publisher,
                i.Version ?? "unknown",
                newest,
                IsOutdated(i.Version, newest),
                i.InstallDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) is { } d ? new DateTimeOffset(d) : null);
        })];
    }

    public async Task<IReadOnlyList<FleetSoftwareEntry>> FindSoftwareAsync(FleetAccess access, SoftwareSearchCriteria criteria, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        Require(access, Permissions.Software.View);
        var rows = InScope(await LoadAsync(access, null, cancellationToken), criteria.Scope);
        var names = rows.ToDictionary(r => r.Device.Id, r => r.Summary.Name);
        var software = await SoftwareByDeviceAsync(access, [.. names.Keys], cancellationToken);
        var latest = await LatestRepositoryVersionsAsync(access, cancellationToken);

        var entries = software
            .SelectMany(kv => kv.Value.Select(app => (DeviceId: kv.Key, App: app)))
            .Where(x => string.IsNullOrWhiteSpace(criteria.Name) || Contains(x.App.Name, criteria.Name.Trim()))
            .Where(x => string.IsNullOrWhiteSpace(criteria.Publisher) || Contains(x.App.Publisher, criteria.Publisher.Trim()))
            .GroupBy(x => (x.App.NameKey, Version: x.App.Version ?? "unknown"))
            .Select(g =>
            {
                var first = g.First().App;
                var newest = Latest(latest, g.Key.NameKey);
                var devices = g.Select(x => x.DeviceId).Distinct().ToList();
                return new FleetSoftwareEntry(
                    first.Name,
                    first.Publisher,
                    g.Key.Version,
                    newest,
                    IsOutdated(first.Version, newest),
                    devices.Count,
                    [.. devices.Take(5).Select(id => names[id])]);
            })
            .Where(e => !criteria.OutdatedOnly || e.IsOutdated)
            .OrderByDescending(e => e.DeviceCount)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, take));
        return [.. entries];
    }

    public async Task<SecurityPosture> GetSecurityPostureAsync(FleetAccess access, FleetScope scope, CancellationToken cancellationToken)
    {
        var rows = InScope(await LoadAsync(access, null, cancellationToken), scope);
        var now = clock.UtcNow;
        var findings = new List<SecurityFinding>();
        var inventories = await InventoriesAsync(access, [.. rows.Select(r => r.Device.Id)], cancellationToken);

        foreach (var row in rows)
        {
            var name = row.Summary.Name;
            inventories.TryGetValue(row.Device.Id, out var inventory);

            if (inventory?.OperatingSystem?.IsSupported == false)
            {
                findings.Add(new SecurityFinding(SecurityFindingKind.OsUnsupported, FindingSeverity.High, row.Device.Id, name,
                    $"{inventory.OperatingSystem.Name ?? row.Summary.OperatingSystem ?? "The operating system"} is no longer supported."));
            }

            var systemDrive = row.Health?.SystemDrive?.TrimEnd('\\');
            var systemVolume = inventory?.Volumes.FirstOrDefault(v => systemDrive is not null && string.Equals(v.DriveLetter?.TrimEnd('\\'), systemDrive, StringComparison.OrdinalIgnoreCase))
                ?? inventory?.Volumes.FirstOrDefault(v => string.Equals(v.DriveLetter?.TrimEnd('\\'), "C:", StringComparison.OrdinalIgnoreCase));
            if (systemVolume?.BitLockerProtected == false)
            {
                findings.Add(new SecurityFinding(SecurityFindingKind.DiskNotEncrypted, FindingSeverity.High, row.Device.Id, name,
                    $"System volume {systemVolume.DriveLetter} is not protected by BitLocker."));
            }

            if (row.Health?.RebootPending == true)
            {
                findings.Add(new SecurityFinding(SecurityFindingKind.PendingReboot, FindingSeverity.Low, row.Device.Id, name,
                    "A restart is pending (updates may not be applied until the device restarts)."));
            }

            var lastSeen = row.Summary.LastSeenUtc ?? row.Device.LastEnrolledAtUtc;
            if (now - lastSeen > StaleAgentAfter)
            {
                findings.Add(new SecurityFinding(SecurityFindingKind.StaleAgent, FindingSeverity.Medium, row.Device.Id, name,
                    $"The agent has not reported for {(int)(now - lastSeen).TotalDays} days."));
            }
        }

        return new SecurityPosture(rows.Count, findings);
    }

    // ------------------------------------------------------------------ data loading

    private sealed record Row(Device Device, DevicePresence? Presence, SystemHealthSnapshot? Health, DeviceSummary Summary);

    private async Task<List<Row>> LoadAsync(FleetAccess access, Guid? deviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        Require(access, Permissions.Devices.View);

        var query = db.Set<Device>().AsNoTracking().Where(d => d.TenantId == access.TenantId && d.Status == DeviceStatus.Active);
        if (deviceId is { } id)
        {
            query = query.Where(d => d.Id == id);
        }

        var devices = await query.ToListAsync(cancellationToken);
        var ids = devices.Select(d => d.Id).ToList();
        var presences = await db.Set<DevicePresence>().AsNoTracking()
            .Where(p => p.TenantId == access.TenantId && ids.Contains(p.DeviceId))
            .ToDictionaryAsync(p => p.DeviceId, cancellationToken);
        var branches = await db.Set<Branch>().AsNoTracking().Where(b => b.TenantId == access.TenantId)
            .ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);
        var departments = await db.Set<Department>().AsNoTracking().Where(d => d.TenantId == access.TenantId)
            .ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);

        return [.. devices.Select(d =>
        {
            presences.TryGetValue(d.Id, out var presence);
            var health = Parse<SystemHealthSnapshot>(presence?.OsHealthJson);
            double? memory = health is { TotalMemoryBytes: > 0, AvailableMemoryBytes: { } available }
                ? Math.Round(100.0 * (health.TotalMemoryBytes.Value - available) / health.TotalMemoryBytes.Value, 1)
                : null;
            var summary = new DeviceSummary(
                d.Id,
                string.IsNullOrWhiteSpace(d.DisplayName) ? d.Hostname : d.DisplayName,
                presence?.State switch
                {
                    ConnectivityState.Online or ConnectivityState.Warning => DeviceConnectivity.Online,
                    ConnectivityState.Offline => DeviceConnectivity.Offline,
                    _ => DeviceConnectivity.Unknown,
                },
                presence?.LastSeenAtUtc,
                d.BranchId,
                d.BranchId is { } b && branches.TryGetValue(b, out var branch) ? branch : null,
                d.DepartmentId,
                d.DepartmentId is { } dep && departments.TryGetValue(dep, out var department) ? department : null,
                d.OsName,
                d.OsDisplayVersion ?? d.OsVersion,
                d.Manufacturer,
                d.Model,
                CpuUsagePercent: null,
                memory,
                health?.SystemDriveFreeBytes,
                health?.SystemDriveTotalBytes);
            return new Row(d, presence, health, summary);
        })];
    }

    private async Task<InventoryReport?> InventoryAsync(FleetAccess access, Guid deviceId, CancellationToken cancellationToken) =>
        (await InventoriesAsync(access, [deviceId], cancellationToken)).GetValueOrDefault(deviceId);

    private async Task<Dictionary<Guid, InventoryReport>> InventoriesAsync(FleetAccess access, IReadOnlyList<Guid> deviceIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, InventoryReport>();
        foreach (var batch in deviceIds.Chunk(200))
        {
            var rows = await db.Set<DeviceInventory>().AsNoTracking()
                .Where(i => i.TenantId == access.TenantId && batch.Contains(i.DeviceId))
                .Select(i => new { i.DeviceId, i.ReportJson })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                if (Parse<InventoryReport>(row.ReportJson) is { } report)
                {
                    result[row.DeviceId] = report;
                }
            }
        }

        return result;
    }

    private async Task<Dictionary<Guid, List<SoftwareInventoryItem>>> SoftwareByDeviceAsync(FleetAccess access, IReadOnlyList<Guid> deviceIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<SoftwareInventoryItem>>();
        foreach (var batch in deviceIds.Chunk(500))
        {
            var items = await db.Set<SoftwareInventoryItem>().AsNoTracking()
                .Where(i => i.TenantId == access.TenantId && batch.Contains(i.DeviceId))
                .ToListAsync(cancellationToken);
            foreach (var group in items.GroupBy(i => i.DeviceId))
            {
                result[group.Key] = [.. group];
            }
        }

        return result;
    }

    /// <summary>Newest ready package version per product in the tenant's own repository ("latest" means latest approved here).</summary>
    private async Task<Dictionary<string, string>> LatestRepositoryVersionsAsync(FleetAccess access, CancellationToken cancellationToken)
    {
        var packages = await db.Set<SoftwarePackage>().AsNoTracking()
            .Where(p => p.TenantId == access.TenantId && p.Status == PackageStatus.Ready && p.Version != null)
            .Select(p => new { p.Name, p.Version })
            .ToListAsync(cancellationToken);
        return packages
            .GroupBy(p => SoftwareInventoryItem.KeyOf(p.Name))
            .ToDictionary(g => g.Key, g => g.Select(p => p.Version!).OrderByDescending(v => v, VersionComparer.Instance).First(), StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static void Require(FleetAccess access, string permission)
    {
        if (!access.Has(permission))
        {
            throw new ForbiddenException($"This needs the {permission} permission.");
        }
    }

    private static List<Row> InScope(List<Row> rows, FleetScope? scope)
    {
        if (scope is null || scope.IsEmpty)
        {
            return rows;
        }

        return [.. rows.Where(r =>
            (scope.BranchId is null || r.Device.BranchId == scope.BranchId)
            && (scope.DepartmentIds is not { Count: > 0 } || (r.Device.DepartmentId is { } d && scope.DepartmentIds.Contains(d))))];
    }

    private static bool Contains(string? value, string search) =>
        value is not null && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static string? Latest(Dictionary<string, string> latest, string nameKey) =>
        latest.TryGetValue(nameKey, out var version) ? version : null;

    private static bool IsOutdated(string? installed, string? newest) =>
        installed is not null && newest is not null && VersionComparer.Instance.Compare(installed, newest) < 0;

    private static T? Parse<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Compares dotted numeric versions ("10.0.19045.1" &lt; "10.0.22631"); anything unparsable compares as equal.</summary>
    internal sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Parts(x);
            var b = Parts(y);
            if (a is null || b is null)
            {
                return 0;
            }

            for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
            {
                var left = i < a.Count ? a[i] : 0;
                var right = i < b.Count ? b[i] : 0;
                if (left != right)
                {
                    return left.CompareTo(right);
                }
            }

            return 0;
        }

        private static List<long>? Parts(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            var parts = new List<long>();
            foreach (var piece in version.Trim().Split('.'))
            {
                var digits = new string([.. piece.TakeWhile(char.IsDigit)]);
                if (digits.Length == 0 || !long.TryParse(digits, out var number))
                {
                    break;
                }

                parts.Add(number);
                if (digits.Length != piece.Length)
                {
                    break;
                }
            }

            return parts.Count == 0 ? null : parts;
        }
    }
}
