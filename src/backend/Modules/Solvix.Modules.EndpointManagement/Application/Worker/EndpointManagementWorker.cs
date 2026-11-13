namespace Solvix.Modules.EndpointManagement.Application.Worker;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.EndpointManagement.Application.Deployments;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Periodically advances deployments for every active tenant. Each tenant runs in
/// its own tenant-bound scope (query filters + RLS), and a failure in one tenant
/// never stops the others. Running several API replicas is safe: state changes
/// use optimistic concurrency, so a lost race only skips one tick.
/// </summary>
internal sealed partial class EndpointManagementWorker(
    IServiceScopeFactory scopes,
    ITenantScopeFactory tenantScopes,
    IClock clock,
    IOptions<EndpointManagementOptions> options,
    ILogger<EndpointManagementWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled)
        {
            LogDisabled(logger);
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.WorkerIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenantIds;
        try
        {
            await using var root = scopes.CreateAsyncScope();
            tenantIds = await root.ServiceProvider.GetRequiredService<ITenantDirectory>().ListActiveTenantIdsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogListFailed(logger, ex);
            return;
        }

        foreach (var tenantId in tenantIds)
        {
            try
            {
                await using var scope = tenantScopes.CreateScope(tenantId);
                await scope.ServiceProvider.GetRequiredService<IDeploymentOrchestrator>().TickAsync(clock.UtcNow, cancellationToken);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException ex)
            {
                LogConcurrency(logger, ex, tenantId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTenantFailed(logger, ex, tenantId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint management worker is disabled by configuration")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Endpoint management worker could not list tenants")]
    private static partial void LogListFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deployment tick for tenant {TenantId} lost a concurrency race; retrying next tick")]
    private static partial void LogConcurrency(ILogger logger, Exception exception, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deployment tick failed for tenant {TenantId}")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);
}
