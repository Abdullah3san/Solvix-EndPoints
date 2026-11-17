namespace Solvix.Modules.Reporting.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IAvailabilityJobRunner
{
    Task<int> RunAsync(CancellationToken cancellationToken);
}

internal sealed partial class AvailabilityJobRunner(IServiceScopeFactory rootScopes, ITenantScopeFactory tenantScopes, ILogger<AvailabilityJobRunner> logger) : IAvailabilityJobRunner
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        List<Guid> tenantIds;
        await using (var scope = rootScopes.CreateAsyncScope())
        {
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantContextMutator>().BeginPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
            tenantIds = await db.Set<Device>().IgnoreQueryFilters().AsNoTracking().Where(d => d.Status == DeviceStatus.Active).Select(d => d.TenantId).Distinct().ToListAsync(cancellationToken);
        }

        var done = 0;
        foreach (var tenantId in tenantIds)
        {
            try
            {
                await using var scope = tenantScopes.CreateScope(tenantId);
                await scope.ServiceProvider.GetRequiredService<AvailabilityRecorder>().RecordAsync(tenantId, cancellationToken);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex, tenantId);
            }
        }

        return done;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Availability sampling failed for tenant {TenantId}")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenantId);
}

/// <summary>Takes the hourly availability samples. A PostgreSQL advisory lock keeps it to one instance per tick.</summary>
internal sealed partial class AvailabilityWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AvailabilityWorker> logger) : BackgroundService
{
    private const long AdvisoryLockKey = 0x534F4C564958_03;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Solvix:Reporting:AvailabilityWorkerEnabled", true))
        {
            return;
        }

        var minutes = Math.Max(5, configuration.GetValue("Solvix:Reporting:AvailabilityIntervalMinutes", 60));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
            do
            {
                await TickAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ISolvixDbContext>();
            // The retrying execution strategy requires user transactions to run inside it.
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(
                async ct =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    var locked = (await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({AdvisoryLockKey}) AS \"Value\"").ToListAsync(ct))[0];
                    if (!locked)
                    {
                        return;
                    }

                    await scope.ServiceProvider.GetRequiredService<IAvailabilityJobRunner>().RunAsync(ct);
                    await transaction.CommitAsync(ct);
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Availability worker tick failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
