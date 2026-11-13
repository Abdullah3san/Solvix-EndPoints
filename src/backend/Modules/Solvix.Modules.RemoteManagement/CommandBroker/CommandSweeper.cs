namespace Solvix.Modules.RemoteManagement.CommandBroker;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.RemoteManagement.Auditing;
using Solvix.Modules.RemoteManagement.Configuration;
using Solvix.Modules.RemoteManagement.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public sealed record CommandSweepResult(int Expired, int TimedOut, int ApprovalsExpired);

/// <summary>
/// Ends commands that can no longer complete: never fetched before their expiry (device offline),
/// waiting for an approval that expired, or acknowledged but silent past their execution timeout
/// (device went offline or crashed while running them). Cross-tenant discovery runs in platform
/// scope; every transition and its audit event are written in the owning tenant's scope.
/// </summary>
public sealed partial class CommandSweeper(
    ISolvixDbContext db,
    ITenantContextMutator tenantMutator,
    ITenantScopeFactory tenantScopes,
    ILogger<CommandSweeper> logger)
{
    private static readonly CommandExecutionStatus[] Waiting =
        [CommandExecutionStatus.PendingApproval, CommandExecutionStatus.Queued, CommandExecutionStatus.Delivered];

    public async Task<CommandSweepResult> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<(Guid TenantId, Guid Id)> candidates;
        using (tenantMutator.BeginPlatformScope())
        {
            // The tenant query filter has no tenant here; RLS bypass (platform scope) allows the cross-tenant read.
            candidates = await db.Set<CommandExecution>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(e => (Waiting.Contains(e.Status) && e.ExpiresAtUtc <= now)
                    || (e.Status == CommandExecutionStatus.Acknowledged && e.ExecutionDeadlineUtc <= now))
                .OrderBy(e => e.TenantId)
                .Take(1000)
                .Select(e => new ValueTuple<Guid, Guid>(e.TenantId, e.Id))
                .ToListAsync(cancellationToken);
        }

        var expired = 0;
        var timedOut = 0;
        var approvals = 0;
        foreach (var tenant in candidates.GroupBy(c => c.TenantId))
        {
            try
            {
                await using var scope = tenantScopes.CreateScope(tenant.Key);
                var (e, t, a) = await ProcessTenantAsync(scope.ServiceProvider, tenant.Select(c => c.Id).ToList(), now, cancellationToken);
                expired += e;
                timedOut += t;
                approvals += a;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTenantFailed(logger, ex, tenant.Key);
            }
        }

        return new CommandSweepResult(expired, timedOut, approvals);
    }

    private static async Task<(int Expired, int TimedOut, int Approvals)> ProcessTenantAsync(
        IServiceProvider services, List<Guid> ids, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<ISolvixDbContext>();
        var audit = services.GetRequiredService<RemoteAuditTrail>();
        return await audit.AtomicAsync(
            async ct =>
            {
                var expired = 0;
                var timedOut = 0;
                var approvals = 0;
                var executions = await db.Set<CommandExecution>().Where(e => ids.Contains(e.Id)).ToListAsync(ct);
                foreach (var execution in executions)
                {
                    if (Waiting.Contains(execution.Status) && execution.ExpiresAtUtc <= now)
                    {
                        if (execution.Status == CommandExecutionStatus.PendingApproval && execution.ApprovalId is { } approvalId)
                        {
                            var approval = await db.Set<RemoteApproval>().FirstAsync(a => a.Id == approvalId, ct);
                            if (approval.Status == ApprovalStatus.Pending)
                            {
                                approval.Status = ApprovalStatus.Expired;
                                approvals++;
                                audit.Record(CommandBroker.Entry(RemoteAuditEvents.ApprovalExpired, RemoteAuditOutcome.Failure, execution) with { ActorUserId = null });
                            }
                        }

                        CommandBroker.Finish(execution, CommandExecutionStatus.Expired, now, "expired", "The device did not fetch the command before it expired.");
                        audit.Record(CommandBroker.Entry(RemoteAuditEvents.CommandExpired, RemoteAuditOutcome.Failure, execution));
                        expired++;
                    }
                    else if (execution.Status == CommandExecutionStatus.Acknowledged && execution.ExecutionDeadlineUtc <= now)
                    {
                        CommandBroker.Finish(execution, CommandExecutionStatus.TimedOut, now, "no_result", "The device accepted the command but did not report a result in time (it may have gone offline).");
                        audit.Record(CommandBroker.Entry(RemoteAuditEvents.CommandTimedOut, RemoteAuditOutcome.Failure, execution));
                        timedOut++;
                    }
                }

                return (expired, timedOut, approvals);
            },
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Command sweep failed for tenant {TenantId}")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);
}

/// <summary>Runs <see cref="CommandSweeper"/> periodically.</summary>
public sealed partial class CommandSweeperService(
    IServiceScopeFactory scopes,
    IOptions<RemoteManagementOptions> options,
    IClock clock,
    ILogger<CommandSweeperService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunSweeper)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.SweepIntervalSeconds, 5, 300)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CommandSweeper>().SweepAsync(clock.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSweepFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Command sweep failed")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
