namespace Solvix.Modules.Devices.Monitoring;

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>
/// Owns every change of a device's monitoring state. All transitions go through
/// <see cref="Transition"/> inside one SaveChanges together with the connection
/// event, the availability ledger and the alert, under optimistic concurrency, so
/// several API nodes (and the monitor) can never record a transition twice.
/// </summary>
public sealed partial class PresenceTracker(
    ISolvixDbContext db,
    ITenantContext tenant,
    MonitoringSettingsProvider settingsProvider,
    PresenceBuffer buffer,
    IMonitoringNotifier notifier,
    IOptions<DevicesOptions> options,
    ILogger<PresenceTracker> logger)
{
    private const int MaxAttempts = 4;

    /// <summary>
    /// Heartbeat path. The common case (online, nothing changed) performs NO
    /// database write: the contact time goes to the in-memory buffer. A write
    /// happens only for a transition, an agent/OS restart, a signal change or
    /// when the stored signal snapshots are due for a refresh.
    /// </summary>
    public async Task<bool> ProcessHeartbeatAsync(Device device, AgentHealthReport health, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(health);
        var settings = await settingsProvider.GetAsync(cancellationToken);
        var networkJson = health.Network is null ? null : JsonSerializer.Serialize(health.Network, DevicesJson.Options);
        var osJson = health.System is null ? null : JsonSerializer.Serialize(health.System, DevicesJson.Options);
        var network = PresencePolicy.ClassifyNetwork(health.Network);
        var os = PresencePolicy.ClassifyOs(health.System);
        var persistEvery = TimeSpan.FromSeconds(options.Value.Monitoring.SignalPersistIntervalSeconds);

        var written = await RunAsync(
            async (unit, ct) =>
            {
                unit.DeviceNames[device.Id] = (device.DisplayName ?? device.Hostname, device.BranchId);
                var presence = await LoadOrCreateAsync(device, now, ct);
                var revision = presence.Revision;
                var isNew = db is DbContext context && context.Entry(presence).State == EntityState.Added;

                if (presence.State != ConnectivityState.Online)
                {
                    await TransitionAsync(presence, ConnectivityState.Online, now, settings, unit, health.ConsecutiveFailures, ct);
                }
                else if (health.ConsecutiveFailures > 0 && !isNew)
                {
                    // Short interruption the agent bridged with retries before any threshold was reached.
                    var since = health.LastSuccessfulContactAtUtc is { } last && last < now ? last : presence.LastSeenAtUtc;
                    unit.Event(new DeviceConnectionEvent(
                        presence.TenantId, presence.DeviceId, ConnectionEventType.Reconnected, MonitoringSignal.Agent, now,
                        detail: $"Agent reconnected after {health.ConsecutiveFailures} failed attempt(s).",
                        lastSeenAt: presence.LastSeenAtUtc,
                        durationSeconds: Seconds(now - since),
                        failedAttempts: health.ConsecutiveFailures));
                }

                var rebooted = presence.SetSystemBooted(health.SystemBootedAtUtc);
                if (rebooted)
                {
                    unit.Event(new DeviceConnectionEvent(
                        presence.TenantId, presence.DeviceId, ConnectionEventType.DeviceRebooted, MonitoringSignal.OperatingSystem, now,
                        detail: "Operating system started at " + health.SystemBootedAtUtc!.Value.ToString("u", CultureInfo.InvariantCulture) + "."));
                }

                var startedAt = health.StartedAtUtc == default ? (DateTimeOffset?)null : health.StartedAtUtc;
                if (presence.SetAgentStarted(startedAt) && !rebooted)
                {
                    unit.Event(new DeviceConnectionEvent(
                        presence.TenantId, presence.DeviceId, ConnectionEventType.AgentRestarted, MonitoringSignal.Agent, now,
                        detail: "Agent service started at " + startedAt!.Value.ToString("u", CultureInfo.InvariantCulture) + "."));
                }

                var signalsChanged = network != presence.NetworkStatus || os != presence.OsHealthStatus;
                if (signalsChanged)
                {
                    RecordSignalEvents(presence, network, os, health, now, unit);
                }

                if (signalsChanged || presence.SignalsPersistedAtUtc is null || now - presence.SignalsPersistedAtUtc >= persistEvery)
                {
                    presence.SetSignals(network, networkJson, os, osJson, now);
                }

                if (!isNew && presence.Revision == revision && !unit.HasChanges)
                {
                    return false;
                }

                presence.Touch(now, successful: true, heartbeat: true);
                await db.SaveChangesAsync(ct);
                return true;
            },
            cancellationToken);

        if (!written)
        {
            buffer.Record(device.TenantId, device.Id, now, successful: true, heartbeat: true);
        }

        return written;
    }

    /// <summary>Called for contact that was buffered for a device that is not (or no longer) online.</summary>
    public async Task RecordContactAsync(Guid deviceId, DateTimeOffset seenAt, bool successful, CancellationToken cancellationToken)
    {
        var settings = await settingsProvider.GetAsync(cancellationToken);
        await RunAsync(
            async (unit, ct) =>
            {
                var presence = await db.Set<DevicePresence>().FirstOrDefaultAsync(p => p.Id == deviceId, ct);
                var active = await db.Set<Device>().AnyAsync(d => d.Id == deviceId && d.Status == DeviceStatus.Active, ct);
                if (presence is null || !active)
                {
                    return false;
                }

                if (presence.State != ConnectivityState.Online && seenAt > presence.StateChangedAtUtc)
                {
                    await TransitionAsync(presence, ConnectivityState.Online, seenAt, settings, unit, failedAttempts: null, ct);
                }

                presence.Touch(seenAt, successful, heartbeat: false);
                await db.SaveChangesAsync(ct);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Monitor path: re-evaluates the given devices of the current tenant. Moves
    /// down (Online → Warning → Offline) only once the thresholds are exceeded
    /// for the effective silence; moves up when contact newer than the last
    /// transition is on record (self-healing for races with the contact flush).
    /// </summary>
    public async Task<int> EvaluateAsync(IReadOnlyCollection<Guid> deviceIds, DateTimeOffset now, DateTimeOffset evidenceFloor, TimeSpan tolerance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (deviceIds.Count == 0)
        {
            return 0;
        }

        var settings = await settingsProvider.GetAsync(cancellationToken);
        var ids = deviceIds.ToList();
        return await RunAsync(
            async (unit, ct) =>
            {
                var activeIds = await db.Set<Device>().Where(d => ids.Contains(d.Id) && d.Status == DeviceStatus.Active).Select(d => d.Id).ToListAsync(ct);
                var presences = await db.Set<DevicePresence>().Where(p => activeIds.Contains(p.Id)).ToListAsync(ct);
                var changed = 0;
                foreach (var presence in presences)
                {
                    var target = PresencePolicy.Evaluate(presence.LastSeenAtUtc, now, settings.Thresholds, evidenceFloor, tolerance);
                    var hasNewerContact = presence.LastSeenAtUtc > presence.StateChangedAtUtc;
                    var goesDown = target > presence.State;
                    var goesUp = target < presence.State && hasNewerContact;
                    if (goesDown || goesUp)
                    {
                        await TransitionAsync(presence, target, now, settings, unit, failedAttempts: null, ct);
                        changed++;
                    }
                }

                if (changed > 0)
                {
                    await db.SaveChangesAsync(ct);
                }

                return changed;
            },
            cancellationToken);
    }

    /// <summary>Closes the availability ledger of every monitored device of the tenant up to <paramref name="until"/> (UTC midnight).</summary>
    public async Task<int> RollOverAsync(DateTimeOffset until, int batchSize, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            var count = await RunAsync(
                async (unit, ct) =>
                {
                    var activeIds = db.Set<Device>().Where(d => d.Status == DeviceStatus.Active).Select(d => d.Id);
                    var presences = await db.Set<DevicePresence>()
                        .Where(p => p.AccountedUntilUtc < until && activeIds.Contains(p.Id))
                        .OrderBy(p => p.Id)
                        .Take(batchSize)
                        .ToListAsync(ct);
                    foreach (var presence in presences)
                    {
                        await unit.AccountAsync(presence, presence.AccountedUntilUtc, until, presence.State, ct);
                        presence.SetAccountedUntil(until);
                    }

                    await db.SaveChangesAsync(ct);
                    return presences.Count;
                },
                cancellationToken);
            total += count;
            if (count < batchSize)
            {
                return total;
            }
        }
    }

    /// <summary>Creates the monitoring record of a newly enrolled device (same unit of work as the enrollment).</summary>
    public void Enroll(Device device, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(device);
        db.Set<DevicePresence>().Add(new DevicePresence(device.TenantId, device.Id, now));
        db.Set<DeviceConnectionEvent>().Add(new DeviceConnectionEvent(
            device.TenantId, device.Id, ConnectionEventType.Enrolled, MonitoringSignal.Agent, now,
            toStatus: nameof(ConnectivityState.Online), detail: "Device enrolled; monitoring started."));
    }

    private async Task<DevicePresence> LoadOrCreateAsync(Device device, DateTimeOffset now, CancellationToken ct)
    {
        var presence = await db.Set<DevicePresence>().FirstOrDefaultAsync(p => p.Id == device.Id, ct);
        if (presence is not null)
        {
            return presence;
        }

        // Devices enrolled before monitoring existed (the migration backfills them; this is the safety net).
        presence = new DevicePresence(device.TenantId, device.Id, now);
        db.Set<DevicePresence>().Add(presence);
        return presence;
    }

    private async Task TransitionAsync(
        DevicePresence presence,
        ConnectivityState to,
        DateTimeOffset now,
        EffectiveMonitoringSettings settings,
        UnitOfWork unit,
        int? failedAttempts,
        CancellationToken ct)
    {
        var from = presence.State;
        if (from == to)
        {
            return;
        }

        var lastSeen = presence.LastSeenAtUtc;
        var start = presence.AccountedUntilUtc;
        if (to == ConnectivityState.Offline)
        {
            // The outage began when the device stopped being heard, not when the timeout fired:
            // time after (last contact + warning threshold) is downtime, even if it was a Warning
            // while we were still giving the device the benefit of the doubt.
            var outageFrom = from == ConnectivityState.Warning
                ? start
                : Max(start, Min(now, lastSeen.AddSeconds(settings.Thresholds.WarningAfterSeconds)));
            await unit.AccountAsync(presence, start, outageFrom, from, ct);
            await unit.AccountAsync(presence, outageFrom, now, ConnectivityState.Offline, ct);
            (await unit.DayAsync(presence, DateOnly.FromDateTime(now.UtcDateTime), ct)).CountOffline();
        }
        else
        {
            await unit.AccountAsync(presence, start, now, from, ct);
        }

        presence.SetAccountedUntil(now);
        var offlineSince = presence.OfflineSinceUtc;
        presence.TransitionTo(to, now);

        var (name, branchId) = await unit.NameAsync(presence.DeviceId, ct);
        unit.StatusChange(new DeviceStatusChangeDto(presence.DeviceId, name, branchId, from, to, now, lastSeen));

        var silence = Seconds(now - lastSeen);
        switch (from, to)
        {
            case (ConnectivityState.Online, ConnectivityState.Warning):
                unit.Event(Event(ConnectionEventType.HeartbeatDelayed, $"No contact for {silence} s (warning after {settings.Thresholds.WarningAfterSeconds} s).", silence));
                break;
            case (_, ConnectivityState.Offline):
                unit.Event(Event(ConnectionEventType.WentOffline, $"No contact for {silence} s (offline after {settings.Thresholds.OfflineAfterSeconds} s).", silence));
                if (settings.OfflineAlertsEnabled)
                {
                    await OpenAlertAsync(presence, name, now, lastSeen, settings, unit, ct);
                }

                break;
            case (ConnectivityState.Warning, ConnectivityState.Online):
                unit.Event(Event(ConnectionEventType.HeartbeatRestored, "Contact resumed before the offline timeout.", silence, failedAttempts));
                break;
            case (ConnectivityState.Offline, ConnectivityState.Online):
                var outage = Seconds(now - (offlineSince ?? lastSeen));
                unit.Event(Event(ConnectionEventType.Recovered, $"Device back online after {FormatDuration(outage)}.", outage, failedAttempts));
                await ResolveAlertsAsync(presence, now, settings, unit, ct);
                break;
            default:
                break;
        }

        DeviceConnectionEvent Event(ConnectionEventType type, string detail, long? duration = null, int? failures = null) =>
            new(presence.TenantId, presence.DeviceId, type, MonitoringSignal.Agent, now, from.ToString(), to.ToString(), detail, lastSeen, duration, failures is > 0 ? failures : null);
    }

    private async Task OpenAlertAsync(DevicePresence presence, string name, DateTimeOffset now, DateTimeOffset lastSeen, EffectiveMonitoringSettings settings, UnitOfWork unit, CancellationToken ct)
    {
        var open = await db.Set<MonitoringAlert>().AnyAsync(a => a.DeviceId == presence.DeviceId && a.Status == MonitoringAlertStatus.Open, ct);
        if (open)
        {
            return;
        }

        var alert = new MonitoringAlert(
            presence.TenantId, presence.DeviceId, name, now, lastSeen,
            $"{name} is offline: no agent contact for more than {FormatDuration(settings.Thresholds.OfflineAfterSeconds)}.");
        db.Set<MonitoringAlert>().Add(alert);
        unit.Alert(alert);
    }

    private async Task ResolveAlertsAsync(DevicePresence presence, DateTimeOffset now, EffectiveMonitoringSettings settings, UnitOfWork unit, CancellationToken ct)
    {
        var alerts = await db.Set<MonitoringAlert>().Where(a => a.DeviceId == presence.DeviceId && a.Status == MonitoringAlertStatus.Open).ToListAsync(ct);
        foreach (var alert in alerts)
        {
            alert.Resolve(now);
            if (settings.RecoveryNotificationsEnabled)
            {
                unit.Alert(alert);
            }
        }
    }

    private static void RecordSignalEvents(DevicePresence presence, NetworkStatus network, OsHealthStatus os, AgentHealthReport health, DateTimeOffset now, UnitOfWork unit)
    {
        // The first report of a signal (Unknown → x) is not news unless it is bad.
        if (network != presence.NetworkStatus && (presence.NetworkStatus != NetworkStatus.Unknown || network is NetworkStatus.Degraded or NetworkStatus.Unreachable))
        {
            var failed = health.Network?.Checks.Where(c => !c.Success).Select(c => $"{c.Kind} {c.Target}: {c.Error ?? "failed"}").ToList() ?? [];
            unit.Event(new DeviceConnectionEvent(
                presence.TenantId, presence.DeviceId, ConnectionEventType.NetworkStatusChanged, MonitoringSignal.Network, now,
                presence.NetworkStatus.ToString(), network.ToString(),
                failed.Count == 0 ? "All network checks succeeded." : string.Join("; ", failed)));
        }

        if (os != presence.OsHealthStatus && (presence.OsHealthStatus != OsHealthStatus.Unknown || os is OsHealthStatus.Warning or OsHealthStatus.Critical))
        {
            var issues = PresencePolicy.DescribeOsIssues(health.System).ToList();
            unit.Event(new DeviceConnectionEvent(
                presence.TenantId, presence.DeviceId, ConnectionEventType.OsHealthChanged, MonitoringSignal.OperatingSystem, now,
                presence.OsHealthStatus.ToString(), os.ToString(),
                issues.Count == 0 ? "No operating-system issues reported." : string.Join("; ", issues)));
        }
    }

    /// <summary>Runs <paramref name="work"/> and retries it on optimistic-concurrency conflicts with fresh data.</summary>
    private async Task<T> RunAsync<T>(Func<UnitOfWork, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var context = (DbContext)db;
        for (var attempt = 1; ; attempt++)
        {
            var unit = new UnitOfWork(db);
            try
            {
                var result = await work(unit, cancellationToken);
                await unit.PublishAsync(tenant.RequireTenantId(), notifier, cancellationToken);
                return result;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && (ex is DbUpdateConcurrencyException || IsUniqueViolation(ex)))
            {
                LogConflict(logger, attempt, ex.GetType().Name);
                context.ChangeTracker.Clear();
            }
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    private static long Seconds(TimeSpan span) => Math.Max(0, (long)span.TotalSeconds);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    internal static string FormatDuration(long seconds) => seconds switch
    {
        < 120 => $"{seconds} s",
        < 7200 => $"{seconds / 60} min",
        < 172_800 => $"{seconds / 3600} h {seconds % 3600 / 60} min",
        _ => $"{seconds / 86400} d {seconds % 86400 / 3600} h",
    };

    [LoggerMessage(Level = LogLevel.Debug, Message = "Presence update conflicted (attempt {Attempt}, {Kind}); retrying with fresh data")]
    private static partial void LogConflict(ILogger logger, int attempt, string kind);

    /// <summary>Collects what one unit of work changed, for the availability ledger and the live update.</summary>
    private sealed class UnitOfWork(ISolvixDbContext db)
    {
        private readonly Dictionary<(Guid, DateOnly), DeviceAvailabilityDay> _days = [];
        private readonly List<DeviceStatusChangeDto> _changes = [];
        private readonly List<DeviceConnectionEvent> _events = [];
        private readonly List<MonitoringAlert> _alerts = [];

        public Dictionary<Guid, (string Name, Guid? BranchId)> DeviceNames { get; } = [];

        public bool HasChanges => _events.Count > 0 || _changes.Count > 0 || _alerts.Count > 0;

        public void Event(DeviceConnectionEvent e)
        {
            db.Set<DeviceConnectionEvent>().Add(e);
            _events.Add(e);
        }

        public void StatusChange(DeviceStatusChangeDto change) => _changes.Add(change);

        public void Alert(MonitoringAlert alert) => _alerts.Add(alert);

        public async Task<(string Name, Guid? BranchId)> NameAsync(Guid deviceId, CancellationToken ct)
        {
            if (DeviceNames.TryGetValue(deviceId, out var known))
            {
                return known;
            }

            var device = await db.Set<Device>().AsNoTracking().Where(d => d.Id == deviceId)
                .Select(d => new { Name = d.DisplayName ?? d.Hostname, d.BranchId })
                .FirstOrDefaultAsync(ct);
            var result = (device?.Name ?? deviceId.ToString(), device?.BranchId);
            DeviceNames[deviceId] = result;
            return result;
        }

        public async Task AccountAsync(DevicePresence presence, DateTimeOffset from, DateTimeOffset to, ConnectivityState bucket, CancellationToken ct)
        {
            foreach (var (day, seconds) in PresencePolicy.SplitByDay(from, to))
            {
                (await DayAsync(presence, day, ct)).Add(bucket, seconds);
            }
        }

        public async Task<DeviceAvailabilityDay> DayAsync(DevicePresence presence, DateOnly day, CancellationToken ct)
        {
            if (_days.TryGetValue((presence.DeviceId, day), out var row))
            {
                return row;
            }

            var deviceId = presence.DeviceId;
            row = await db.Set<DeviceAvailabilityDay>().FirstOrDefaultAsync(a => a.DeviceId == deviceId && a.Day == day, ct);
            if (row is null)
            {
                row = new DeviceAvailabilityDay(presence.TenantId, deviceId, day);
                db.Set<DeviceAvailabilityDay>().Add(row);
            }

            _days[(deviceId, day)] = row;
            return row;
        }

        public async Task PublishAsync(Guid tenantId, IMonitoringNotifier notifier, CancellationToken ct)
        {
            if (!HasChanges)
            {
                return;
            }

            var events = new List<ConnectionEventDto>();
            foreach (var e in _events)
            {
                var (name, _) = await NameAsync(e.DeviceId, ct);
                events.Add(MonitoringQueries.ToDto(e, name));
            }

            await notifier.PublishAsync(tenantId, new MonitoringUpdateDto(_changes, events, _alerts.Select(MonitoringAlertDto.From).ToList()), ct);
        }
    }
}
