namespace Solvix.Modules.Mdm.Application.Maintenance;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Mdm.Application.Devices;
using Solvix.Modules.Mdm.Domain;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Periodic, tenant-by-tenant upkeep: inventory sync, command reconciliation and expiry, compliance re-evaluation
/// (which also catches devices that silently stopped checking in). Each tenant runs in its own tenant-bound scope.
/// </summary>
internal sealed class MdmSyncWorker(
    IServiceScopeFactory rootScopes,
    ITenantScopeFactory tenantScopes,
    IOptions<MdmOptions> options,
    ILogger<MdmSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sync = options.Value.Sync;
        if (!sync.Enabled)
        {
            logger.LogInformation("MDM sync worker is disabled (Solvix:Mdm:Sync:Enabled=false)");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(sync.IntervalMinutes));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "MDM sync pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var tenantId in await ListTenantsAsync(cancellationToken))
        {
            try
            {
                await using var scope = tenantScopes.CreateScope(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<IDeviceSyncService>().SyncAsync(cancellationToken);
                logger.LogInformation("MDM sync for tenant {TenantId}: {@Result}", tenantId, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One tenant's provider outage must not stop the others.
                logger.LogError(ex, "MDM sync failed for tenant {TenantId}", tenantId);
            }
        }
    }

    private async Task<List<Guid>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = rootScopes.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantContextMutator>().BeginPlatformScope();
        var db = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
        var withDevices = await db.Set<MobileDevice>().IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.State == DeviceState.Enrolled || d.State == DeviceState.RemovalPending)
            .Select(d => d.TenantId).Distinct().ToListAsync(cancellationToken);
        var withEnterprise = await db.Set<MobileEnterprise>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.EnterpriseName != null).Select(e => e.TenantId).Distinct().ToListAsync(cancellationToken);
        return withDevices.Union(withEnterprise).ToList();
    }
}
