namespace Solvix.Modules.Devices.Monitoring;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Solvix.Modules.Devices.Configuration;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>Effective monitoring settings of the current tenant (tenant row or platform defaults).</summary>
public sealed record EffectiveMonitoringSettings(
    MonitoringThresholds Thresholds,
    bool OfflineAlertsEnabled,
    bool RecoveryNotificationsEnabled,
    bool IsDefault)
{
    public MonitoringSettingsDto ToDto() => new(
        Thresholds.HeartbeatIntervalSeconds,
        Thresholds.WarningAfterSeconds,
        Thresholds.OfflineAfterSeconds,
        OfflineAlertsEnabled,
        RecoveryNotificationsEnabled,
        IsDefault);
}

/// <summary>
/// Reads tenant settings on the heartbeat path, so they are cached briefly in
/// memory. A change is visible on the node that made it immediately and on
/// other nodes within <see cref="CacheDuration"/>.
/// </summary>
public sealed class MonitoringSettingsProvider(
    ISolvixDbContext db,
    ITenantContext tenant,
    IMemoryCache cache,
    IOptions<DevicesOptions> options)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task<EffectiveMonitoringSettings> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        if (cache.TryGetValue(Key(tenantId), out EffectiveMonitoringSettings? cached) && cached is not null)
        {
            return cached;
        }

        var row = await db.Set<MonitoringSettings>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var effective = row is null ? Defaults(options.Value) : From(row);
        cache.Set(Key(tenantId), effective, CacheDuration);
        return effective;
    }

    public void Invalidate(Guid tenantId) => cache.Remove(Key(tenantId));

    public static EffectiveMonitoringSettings Defaults(DevicesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new EffectiveMonitoringSettings(
            new MonitoringThresholds(options.Agent.HeartbeatIntervalSeconds, options.Monitoring.DefaultWarningAfterSeconds, options.Monitoring.DefaultOfflineAfterSeconds),
            OfflineAlertsEnabled: true,
            RecoveryNotificationsEnabled: true,
            IsDefault: true);
    }

    public static EffectiveMonitoringSettings From(MonitoringSettings row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new EffectiveMonitoringSettings(
            new MonitoringThresholds(row.HeartbeatIntervalSeconds, row.WarningAfterSeconds, row.OfflineAfterSeconds),
            row.OfflineAlertsEnabled,
            row.RecoveryNotificationsEnabled,
            IsDefault: false);
    }

    private static string Key(Guid tenantId) => "monitoring-settings:" + tenantId.ToString("N");
}
