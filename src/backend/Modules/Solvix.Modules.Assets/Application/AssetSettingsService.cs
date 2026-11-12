namespace Solvix.Modules.Assets.Application;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Assets.Domain;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IAssetSettingsService
{
    Task<AssetSettingsDto> GetAsync(CancellationToken cancellationToken);

    Task<AssetSettingsDto> UpdateAsync(AssetSettingsDto request, CancellationToken cancellationToken);
}

/// <summary>Per-tenant settings; defaults apply until an administrator saves them. Cached for the scope.</summary>
internal sealed class AssetSettingsService(ISolvixDbContext db, ITenantContext tenant, AssetAuditTrail audit) : IAssetSettingsService
{
    private AssetSettingsDto? _cached;

    public async Task<AssetSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var row = await db.Set<AssetSettings>().AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? AssetSettings.CreateDefault(Guid.Empty);
        return _cached = ToDto(row);
    }

    public async Task<AssetSettingsDto> UpdateAsync(AssetSettingsDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var row = await db.Set<AssetSettings>().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = AssetSettings.CreateDefault(tenant.RequireTenantId());
            db.Set<AssetSettings>().Add(row);
        }

        var before = ToDto(row);
        row.Update(request.TagPrefix, request.DefaultCurrency, request.AutoApproveHighConfidenceMatches, request.WarrantyAlertDays);
        var after = ToDto(row);
        audit.Record(
            AssetAuditTrail.SettingsEntity, row.Id, null, "settings.updated", "Asset management settings updated.",
            AssetAuditTrail.Diff(Flatten(before), Flatten(after)));
        await AssetPersistence.SaveAsync(db, cancellationToken);
        return _cached = after;
    }

    private static AssetSettingsDto ToDto(AssetSettings s) => new(s.TagPrefix, s.DefaultCurrency, s.AutoApproveHighConfidenceMatches, s.WarrantyAlertDays);

    private static Dictionary<string, string?> Flatten(AssetSettingsDto s) => new(StringComparer.Ordinal)
    {
        ["tagPrefix"] = s.TagPrefix,
        ["defaultCurrency"] = s.DefaultCurrency,
        ["autoApproveHighConfidenceMatches"] = s.AutoApproveHighConfidenceMatches.ToString(),
        ["warrantyAlertDays"] = s.WarrantyAlertDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
