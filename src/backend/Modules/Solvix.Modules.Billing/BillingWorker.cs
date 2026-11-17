namespace Solvix.Modules.Billing;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Billing.Application;
using Solvix.Modules.Billing.Configuration;
using Solvix.SharedKernel.Persistence;

/// <summary>
/// Runs <see cref="IBillingJobRunner"/> on a timer. A PostgreSQL advisory lock makes exactly one API instance
/// do the work per tick, so scaling out never multiplies it.
/// </summary>
internal sealed partial class BillingWorker(IServiceScopeFactory scopes, IOptions<BillingOptions> options, ILogger<BillingWorker> logger) : BackgroundService
{
    private const long AdvisoryLockKey = 0x534F4C564958_01; // "SOLVIX" + 01

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = options.Value.Worker;
        if (!worker.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, worker.IntervalMinutes)));
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

                    await scope.ServiceProvider.GetRequiredService<IBillingJobRunner>().RunAsync(ct);
                    await transaction.CommitAsync(ct);
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTickFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Billing worker tick failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
