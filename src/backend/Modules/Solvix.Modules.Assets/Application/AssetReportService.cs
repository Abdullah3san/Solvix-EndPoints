namespace Solvix.Modules.Assets.Application;

using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Assets.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Persistence;

public interface IAssetReportService
{
    Task<AssetSummaryReportDto> GetSummaryAsync(CancellationToken cancellationToken);

    Task<DepreciationReportDto> GetDepreciationAsync(DateOnly? asOf, CancellationToken cancellationToken);

    Task<string> ExportDepreciationCsvAsync(DateOnly? asOf, CancellationToken cancellationToken);
}

internal sealed class AssetReportService(
    ISolvixDbContext db,
    IEndpointDirectory endpoints,
    IAssetSettingsService settings,
    AssetReferences references,
    IClock clock) : IAssetReportService
{
    public async Task<AssetSummaryReportDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var config = await settings.GetAsync(cancellationToken);
        var assets = await db.Set<Asset>().AsNoTracking().ToListAsync(cancellationToken);
        var active = assets.Where(a => a.Status is not (AssetStatus.Retired or AssetStatus.Disposed)).ToList();

        var byStatus = assets.GroupBy(a => a.Status).OrderBy(g => g.Key)
            .Select(g => new CountValueDto(g.Key.ToString(), null, g.Count(), g.Sum(a => a.PurchaseCost ?? 0m))).ToList();
        var byCategory = assets.GroupBy(a => a.Category).OrderByDescending(g => g.Count())
            .Select(g => new CountValueDto(g.Key.ToString(), AssetCategories.Get(g.Key).DisplayName, g.Count(), g.Sum(a => a.PurchaseCost ?? 0m))).ToList();
        await references.PreloadAsync([], active.Select(a => a.BranchId), [], [], cancellationToken);
        var byBranch = active.GroupBy(a => a.BranchId).OrderByDescending(g => g.Count())
            .Select(g => new CountValueDto(g.Key?.ToString() ?? "none", g.Key is null ? "No branch" : references.Branch(g.Key)?.Name, g.Count(), g.Sum(a => a.PurchaseCost ?? 0m))).ToList();

        var warranties = active.Select(a => Asset.WarrantyStateOn(a.WarrantyEndDate, today, config.WarrantyAlertDays)).ToList();
        var yearStart = new DateTimeOffset(today.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var maintenanceCost = await db.Set<MaintenanceRecord>().Where(m => m.Status == MaintenanceStatus.Completed && m.CompletedAtUtc >= yearStart).SumAsync(m => m.Cost ?? 0m, cancellationToken);
        var openMaintenance = await db.Set<MaintenanceRecord>().CountAsync(m => m.Status == MaintenanceStatus.InProgress || m.Status == MaintenanceStatus.Scheduled, cancellationToken);
        var auditCutoff = clock.UtcNow.AddDays(-365);

        var manageable = active.Where(a => AssetCategories.Get(a.Category).EndpointManageable).Select(a => a.Id).ToHashSet();
        var approved = await db.Set<AssetDeviceLink>().AsNoTracking().Where(l => l.Status == AssetDeviceLinkStatus.Approved).Select(l => new { l.AssetId, l.DeviceId }).ToListAsync(cancellationToken);
        var pending = await db.Set<AssetDeviceLink>().CountAsync(l => l.Status == AssetDeviceLinkStatus.Suggested, cancellationToken);
        var activeEndpoints = await endpoints.CountActiveAsync(cancellationToken);
        var linkedDevices = await endpoints.FindAsync(approved.Select(l => l.DeviceId).ToList(), cancellationToken);
        var linkedActiveEndpoints = linkedDevices.Values.Count(d => d.IsActive);

        return new AssetSummaryReportDto(
            today,
            assets.Count,
            active.Count,
            assets.Where(a => a.Status != AssetStatus.Disposed).Sum(a => a.PurchaseCost ?? 0m),
            assets.Where(a => a.Status != AssetStatus.Disposed).Sum(a => DepreciationCalculator.Calculate(a, today)?.BookValue ?? a.PurchaseCost ?? 0m),
            byStatus,
            byCategory,
            byBranch,
            warranties.Count(w => w == WarrantyState.Active),
            warranties.Count(w => w == WarrantyState.ExpiringSoon),
            warranties.Count(w => w == WarrantyState.Expired),
            warranties.Count(w => w == WarrantyState.None),
            openMaintenance,
            maintenanceCost,
            active.Count(a => a.LastAuditedAtUtc is null || a.LastAuditedAtUtc < auditCutoff),
            new EndpointCoverageDto(
                manageable.Count,
                approved.Count(l => manageable.Contains(l.AssetId)),
                pending,
                activeEndpoints,
                Math.Max(0, activeEndpoints - linkedActiveEndpoints)));
    }

    public async Task<DepreciationReportDto> GetDepreciationAsync(DateOnly? asOf, CancellationToken cancellationToken)
    {
        var date = asOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var rows = (await LoadDepreciableAsync(date, cancellationToken))
            .GroupBy(x => x.Asset.Category)
            .OrderBy(g => g.Key)
            .Select(g => new DepreciationReportRowDto(g.Key, g.Count(), g.Sum(x => x.Result.Cost), g.Sum(x => x.Result.AccumulatedDepreciation), g.Sum(x => x.Result.BookValue)))
            .ToList();
        return new DepreciationReportDto(date, rows, rows.Sum(r => r.Cost), rows.Sum(r => r.AccumulatedDepreciation), rows.Sum(r => r.BookValue));
    }

    public async Task<string> ExportDepreciationCsvAsync(DateOnly? asOf, CancellationToken cancellationToken)
    {
        var date = asOf ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var csv = new StringBuilder();
        Csv.Row(csv, "Asset tag", "Name", "Category", "Status", "Method", "Purchase date", "Cost", "Salvage value", "Useful life (months)", "Months elapsed", "Accumulated depreciation", "Book value", "Fully depreciated on", "As of");
        foreach (var (asset, r) in (await LoadDepreciableAsync(date, cancellationToken)).OrderBy(x => x.Asset.AssetTag, StringComparer.Ordinal))
        {
            Csv.Row(
                csv,
                asset.AssetTag,
                asset.Name,
                AssetCategories.Get(asset.Category).DisplayName,
                asset.Status.ToString(),
                r.Method.ToString(),
                r.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Cost.ToString("0.00", CultureInfo.InvariantCulture),
                r.SalvageValue.ToString("0.00", CultureInfo.InvariantCulture),
                r.UsefulLifeMonths.ToString(CultureInfo.InvariantCulture),
                r.MonthsElapsed.ToString(CultureInfo.InvariantCulture),
                r.AccumulatedDepreciation.ToString("0.00", CultureInfo.InvariantCulture),
                r.BookValue.ToString("0.00", CultureInfo.InvariantCulture),
                r.FullyDepreciatedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return csv.ToString();
    }

    private async Task<List<(Asset Asset, DepreciationResult Result)>> LoadDepreciableAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var assets = await db.Set<Asset>().AsNoTracking()
            .Where(a => a.DepreciationMethod != DepreciationMethod.None && a.PurchaseDate <= asOf)
            .ToListAsync(cancellationToken);
        return assets
            .Where(a => a.DisposedAtUtc is not { } disposed || DateOnly.FromDateTime(disposed.UtcDateTime) > asOf)
            .Select(a => (Asset: a, Result: DepreciationCalculator.Calculate(a, asOf)))
            .Where(x => x.Result is not null)
            .Select(x => (x.Asset, x.Result!))
            .ToList();
    }
}
