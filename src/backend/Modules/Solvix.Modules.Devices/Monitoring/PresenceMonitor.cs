namespace Solvix.Modules.Devices.Monitoring;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Solvix.Modules.Devices.Configuration;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public sealed record SweepResult(bool Ran, int Candidates, int Transitions, bool OutageDetected, DateTimeOffset EvidenceFloorUtc);

/// <summary>
/// One monitoring pass: flush buffered contact, detect platform outages, find
/// devices whose silence crossed a threshold (one indexed query for the whole
/// platform), apply transitions per tenant inside tenant scopes, roll the
/// availability ledger over at UTC midnight and purge expired history.
/// Only one node sweeps at a time (PostgreSQL advisory lock).
/// </summary>
public sealed partial class PresenceMonitor(
    ISolvixDbContext db,
    ITenantContextMutator tenantMutator,
    ITenantScopeFactory tenantScopes,
    PresenceBuffer buffer,
    IConfiguration configuration,
    IOptions<DevicesOptions> options,
    ILogger<PresenceMonitor> logger)
{
    private const long AdvisoryLockKey = 0x5_0_1_7_1_C_0_4; // "solvix monitoring"

    public async Task<SweepResult> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value.Monitoring;
        await buffer.FlushAsync(cancellationToken);

        await using var lockConnection = new NpgsqlConnection(configuration.GetConnectionString("Postgres"));
        await lockConnection.OpenAsync(cancellationToken);
        if (!await TryLockAsync(lockConnection, cancellationToken))
        {
            return new SweepResult(false, 0, 0, false, now);
        }

        try
        {
            var (floor, outage) = await AdvanceCheckpointAsync(now, settings, cancellationToken);
            var tolerance = TimeSpan.FromSeconds(settings.FlushIntervalSeconds + 2);
            var candidates = await FindCandidatesAsync(now - tolerance, floor, settings, cancellationToken);
            var transitions = 0;
            foreach (var group in candidates.GroupBy(c => c.TenantId))
            {
                var ids = group.Select(c => c.Id).ToList();
                try
                {
                    transitions += await InTenantAsync(group.Key, (tracker, ct) => tracker.EvaluateAsync(ids, now, floor, tolerance, ct), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogTenantFailed(logger, ex, group.Key);
                }
            }

            await RollOverAsync(now, settings, cancellationToken);
            await PurgeAsync(now, settings, cancellationToken);
            return new SweepResult(true, candidates.Count, transitions, outage, floor);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", lockConnection);
            unlock.Parameters.AddWithValue("key", AdvisoryLockKey);
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> TryLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// If the previous sweep is older than the outage threshold, nobody was
    /// listening: agents may have been failing against a dead platform and are now
    /// somewhere in their retry backoff. Silence is then only counted from now plus
    /// a grace proportional to the outage, so a platform outage never turns into a
    /// wave of false offline alerts.
    /// </summary>
    private async Task<(DateTimeOffset Floor, bool Outage)> AdvanceCheckpointAsync(DateTimeOffset now, MonitoringOptions settings, CancellationToken ct)
    {
        using var platform = tenantMutator.BeginPlatformScope();
        var checkpoint = await db.Set<MonitorCheckpoint>().FirstOrDefaultAsync(c => c.Name == MonitorCheckpoint.PresenceMonitor, ct);
        var outage = false;
        if (checkpoint is null)
        {
            // First sweep ever (fresh install or upgrade): give everyone a full threshold from now.
            checkpoint = new MonitorCheckpoint(MonitorCheckpoint.PresenceMonitor, now);
            db.Set<MonitorCheckpoint>().Add(checkpoint);
        }
        else
        {
            var gap = now - checkpoint.LastSweepAtUtc;
            if (gap > TimeSpan.FromSeconds(settings.OutageDetectionSeconds))
            {
                outage = true;
                var grace = TimeSpan.FromSeconds(Math.Min(gap.TotalSeconds, settings.MaxRecoveryGraceSeconds));
                checkpoint.ObservingSinceUtc = now;
                checkpoint.EvidenceFloorUtc = now + grace;
                LogOutage(logger, (long)gap.TotalSeconds, (long)grace.TotalSeconds);
            }

            if (now > checkpoint.LastSweepAtUtc)
            {
                checkpoint.LastSweepAtUtc = now;
            }
        }

        await db.SaveChangesAsync(ct);
        return (checkpoint.EvidenceFloorUtc, outage);
    }

    private async Task<List<Candidate>> FindCandidatesAsync(DateTimeOffset reference, DateTimeOffset floor, MonitoringOptions settings, CancellationToken ct)
    {
        using var platform = tenantMutator.BeginPlatformScope();
        var defaults = MonitoringSettingsProvider.Defaults(options.Value).Thresholds;

        // Pre-selection only; PresencePolicy.Evaluate makes the decision per device under its tenant's RLS scope.
        return await db.Database.SqlQueryRaw<Candidate>(
            """
            SELECT p.tenant_id AS "TenantId", p.id AS "Id"
            FROM device_presence p
            JOIN devices d ON d.id = p.id AND d.tenant_id = p.tenant_id
            LEFT JOIN monitoring_settings s ON s.tenant_id = p.tenant_id
            WHERE d.status = 'Active' AND (
                   (p.state = 'Online'  AND GREATEST(p.last_seen_at_utc, @floor) <= @reference - make_interval(secs => COALESCE(s.warning_after_seconds, @warning)))
                OR (p.state = 'Warning' AND GREATEST(p.last_seen_at_utc, @floor) <  @reference - make_interval(secs => COALESCE(s.offline_after_seconds, @offline)))
                OR (p.state <> 'Online' AND p.last_seen_at_utc > p.state_changed_at_utc))
            ORDER BY p.tenant_id
            LIMIT @limit
            """,
            new NpgsqlParameter("floor", floor),
            new NpgsqlParameter("reference", reference),
            new NpgsqlParameter("warning", defaults.WarningAfterSeconds),
            new NpgsqlParameter("offline", defaults.OfflineAfterSeconds),
            new NpgsqlParameter("limit", settings.SweepBatchSize)).ToListAsync(ct);
    }

    private async Task RollOverAsync(DateTimeOffset now, MonitoringOptions settings, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        List<Guid> tenants;
        using (tenantMutator.BeginPlatformScope())
        {
            var checkpoint = await db.Set<MonitorCheckpoint>().FirstAsync(c => c.Name == MonitorCheckpoint.PresenceMonitor, ct);
            if (checkpoint.LastRollupDay >= today)
            {
                return;
            }

            tenants = await db.Set<DevicePresence>().IgnoreQueryFilters().Select(p => p.TenantId).Distinct().ToListAsync(ct);
            checkpoint.LastRollupDay = today;
            await db.SaveChangesAsync(ct);
        }

        var midnight = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        foreach (var tenantId in tenants)
        {
            try
            {
                await InTenantAsync(tenantId, (tracker, token) => tracker.RollOverAsync(midnight, settings.SweepBatchSize, token), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTenantFailed(logger, ex, tenantId);
            }
        }
    }

    private async Task PurgeAsync(DateTimeOffset now, MonitoringOptions settings, CancellationToken ct)
    {
        using var platform = tenantMutator.BeginPlatformScope();
        var checkpoint = await db.Set<MonitorCheckpoint>().FirstAsync(c => c.Name == MonitorCheckpoint.PresenceMonitor, ct);
        if (now - checkpoint.LastPurgeAtUtc < TimeSpan.FromHours(1))
        {
            return;
        }

        checkpoint.LastPurgeAtUtc = now;
        await db.SaveChangesAsync(ct);

        var eventsBefore = now.AddDays(-settings.EventRetentionDays);
        var daysBefore = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-settings.AvailabilityRetentionDays));
        var events = await db.Set<DeviceConnectionEvent>().IgnoreQueryFilters().Where(e => e.OccurredAtUtc < eventsBefore).ExecuteDeleteAsync(ct);
        var alerts = await db.Set<MonitoringAlert>().IgnoreQueryFilters()
            .Where(a => a.Status == MonitoringAlertStatus.Resolved && a.ResolvedAtUtc < eventsBefore).ExecuteDeleteAsync(ct);
        var days = await db.Set<DeviceAvailabilityDay>().IgnoreQueryFilters().Where(a => a.Day < daysBefore).ExecuteDeleteAsync(ct);
        if (events + alerts + days > 0)
        {
            LogPurged(logger, events, alerts, days);
        }
    }

    private async Task<int> InTenantAsync(Guid tenantId, Func<PresenceTracker, CancellationToken, Task<int>> work, CancellationToken ct)
    {
        await using var scope = tenantScopes.CreateScope(tenantId);
        return await work(scope.ServiceProvider.GetRequiredService<PresenceTracker>(), ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Presence monitor was not running for {GapSeconds} s (platform outage or restart); devices get {GraceSeconds} s extra before silence counts")]
    private static partial void LogOutage(ILogger logger, long gapSeconds, long graceSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Presence evaluation failed for tenant {TenantId}")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged monitoring history: {Events} events, {Alerts} alerts, {Days} availability days")]
    private static partial void LogPurged(ILogger logger, int events, int alerts, int days);

    private sealed record Candidate(Guid TenantId, Guid Id);
}

/// <summary>Flushes buffered contact on every node; sweeps on the node holding the advisory lock.</summary>
internal sealed partial class MonitoringBackgroundService(
    IServiceScopeFactory scopes,
    PresenceBuffer buffer,
    IClock clock,
    IOptions<DevicesOptions> options,
    ILogger<MonitoringBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Monitoring;
        var flushInterval = TimeSpan.FromSeconds(settings.FlushIntervalSeconds);
        var sweepInterval = TimeSpan.FromSeconds(settings.SweepIntervalSeconds);
        var nextSweep = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(flushInterval < sweepInterval ? flushInterval : sweepInterval);
        try
        {
            do
            {
                try
                {
                    if (settings.RunMonitor && DateTimeOffset.UtcNow >= nextSweep)
                    {
                        nextSweep = DateTimeOffset.UtcNow + sweepInterval;
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<PresenceMonitor>().SweepAsync(clock.UtcNow, stoppingToken);
                    }
                    else
                    {
                        await buffer.FlushAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCycleFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Graceful shutdown: do not drop the contact evidence collected since the last flush.
        try
        {
            await buffer.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCycleFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Monitoring cycle failed")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
