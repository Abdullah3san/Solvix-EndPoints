namespace Solvix.Modules.Helpdesk;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Helpdesk.Application;
using Solvix.Modules.Helpdesk.Configuration;
using Solvix.SharedKernel.Persistence;

/// <summary>SLA breach detection and escalation on a timer; a PostgreSQL advisory lock keeps it to one instance per tick.</summary>
internal sealed partial class HelpdeskWorker(IServiceScopeFactory scopes, IOptions<HelpdeskOptions> options, ILogger<HelpdeskWorker> logger) : BackgroundService
{
    private const long AdvisoryLockKey = 0x534F4C564958_02;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = options.Value.Worker;
        if (!worker.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
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

                    await scope.ServiceProvider.GetRequiredService<IHelpdeskJobRunner>().RunAsync(ct);
                    await transaction.CommitAsync(ct);
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Helpdesk worker tick failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
