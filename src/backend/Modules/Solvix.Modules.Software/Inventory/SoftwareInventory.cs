namespace Solvix.Modules.Software.Inventory;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Software.Domain;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public sealed record SoftwareCatalogEntryDto(string Name, string? Publisher, int DeviceCount, IReadOnlyList<string> Versions);

public sealed record DeviceSoftwareDto(Guid DeviceId, string Name, string? Version, string? Publisher, string? Scope, DateOnly? InstallDate, DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc);

public sealed record SoftwareInstallationDto(Guid DeviceId, string DeviceName, bool DeviceActive, string Name, string? Version, string? Publisher, DateTimeOffset LastSeenAtUtc);

public sealed record CatalogQuery
{
    public string? Search { get; init; }

    public string? Publisher { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;
}

public interface ISoftwareInventoryQueries
{
    Task<PagedResult<SoftwareCatalogEntryDto>> CatalogAsync(CatalogQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceSoftwareDto>> ForDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    /// <summary>Devices that have an application installed (exact name, case-insensitive), optionally a specific version.</summary>
    Task<IReadOnlyList<SoftwareInstallationDto>> InstallationsAsync(string name, string? version, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps one row per installed application and device, replaced from every inventory upload
/// (rows that disappeared are removed; first-seen dates survive). Runs in the device's tenant scope.
/// </summary>
internal sealed class SoftwareInventoryProjector(ISolvixDbContext db, ITenantContext tenantContext) : ISoftwareInventoryObserver
{
    public const int MaxNameLength = 256;

    public async Task OnSoftwareInventoryAsync(Guid deviceId, DateTimeOffset collectedAtUtc, IReadOnlyList<InstalledSoftwareEntry> software, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(software);
        var tenantId = tenantContext.RequireTenantId();
        var reported = software
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => s with { Name = Clip(s.Name.Trim(), MaxNameLength)!, Version = Clip(s.Version?.Trim(), 128), Publisher = Clip(s.Publisher?.Trim(), 256), Scope = Clip(s.Scope, 16) })
            .GroupBy(s => (SoftwareInventoryItem.KeyOf(s.Name), s.Version ?? string.Empty, s.Scope ?? string.Empty))
            .ToDictionary(g => g.Key, g => g.First());
        var existing = await db.Set<SoftwareInventoryItem>().Where(i => i.DeviceId == deviceId).ToListAsync(cancellationToken);
        var byKey = existing.GroupBy(i => (i.NameKey, i.Version ?? string.Empty, i.Scope ?? string.Empty)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (key, entry) in reported)
        {
            if (byKey.Remove(key, out var rows))
            {
                rows[0].Seen(entry.Publisher, entry.InstallDate, collectedAtUtc);
                db.Set<SoftwareInventoryItem>().RemoveRange(rows.Skip(1));
            }
            else
            {
                db.Set<SoftwareInventoryItem>().Add(new SoftwareInventoryItem(tenantId, deviceId, entry.Name, entry.Version, entry.Publisher, entry.Scope, entry.InstallDate, collectedAtUtc));
            }
        }

        db.Set<SoftwareInventoryItem>().RemoveRange(byKey.Values.SelectMany(v => v));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? Clip(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

internal sealed class SoftwareInventoryQueries(ISolvixDbContext db, IEndpointDirectory endpoints) : ISoftwareInventoryQueries
{
    public async Task<PagedResult<SoftwareCatalogEntryDto>> CatalogAsync(CatalogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 200);
        var items = db.Set<SoftwareInventoryItem>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            items = items.Where(i => EF.Functions.ILike(i.Name, pattern) || (i.Publisher != null && EF.Functions.ILike(i.Publisher, pattern)));
        }

        if (!string.IsNullOrWhiteSpace(query.Publisher))
        {
            var publisher = query.Publisher.Trim();
            items = items.Where(i => i.Publisher != null && EF.Functions.ILike(i.Publisher, publisher));
        }

        var grouped = items.GroupBy(i => i.NameKey).Select(g => new
        {
            Key = g.Key,
            Name = g.Max(i => i.Name)!,
            Publisher = g.Max(i => i.Publisher),
            Devices = g.Select(i => i.DeviceId).Distinct().Count(),
        });
        var total = await grouped.LongCountAsync(cancellationToken);
        var rows = await grouped.OrderByDescending(g => g.Devices).ThenBy(g => g.Key).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        var keys = rows.Select(r => r.Key).ToList();
        var versions = await db.Set<SoftwareInventoryItem>().AsNoTracking()
            .Where(i => keys.Contains(i.NameKey) && i.Version != null)
            .Select(i => new { i.NameKey, i.Version })
            .Distinct()
            .ToListAsync(cancellationToken);
        var byKey = versions.ToLookup(v => v.NameKey, v => v.Version!);
        return new PagedResult<SoftwareCatalogEntryDto>(
            rows.Select(r => new SoftwareCatalogEntryDto(r.Name, r.Publisher, r.Devices,
                byKey[r.Key].OrderByDescending(v => v, Agent.Contracts.Software.SoftwareVersion.Comparer).Take(20).ToList())).ToList(),
            page,
            size,
            total);
    }

    public async Task<IReadOnlyList<DeviceSoftwareDto>> ForDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        await db.Set<SoftwareInventoryItem>().AsNoTracking()
            .Where(i => i.DeviceId == deviceId)
            .OrderBy(i => i.NameKey)
            .Select(i => new DeviceSoftwareDto(i.DeviceId, i.Name, i.Version, i.Publisher, i.Scope, i.InstallDate, i.FirstSeenAtUtc, i.LastSeenAtUtc))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SoftwareInstallationDto>> InstallationsAsync(string name, string? version, CancellationToken cancellationToken)
    {
        var key = SoftwareInventoryItem.KeyOf(name ?? string.Empty);
        var items = db.Set<SoftwareInventoryItem>().AsNoTracking().Where(i => i.NameKey == key);
        if (!string.IsNullOrWhiteSpace(version))
        {
            items = items.Where(i => i.Version == version.Trim());
        }

        var rows = await items.OrderBy(i => i.DeviceId).Take(5000).ToListAsync(cancellationToken);
        var devices = await endpoints.FindAsync(rows.Select(r => r.DeviceId).Distinct().ToList(), cancellationToken);
        return rows.Select(r =>
        {
            var device = devices.GetValueOrDefault(r.DeviceId);
            return new SoftwareInstallationDto(r.DeviceId, device?.Hostname ?? "unknown", device?.IsActive ?? false, r.Name, r.Version, r.Publisher, r.LastSeenAtUtc);
        }).OrderBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
