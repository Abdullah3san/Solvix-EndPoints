namespace Solvix.Modules.Devices.Monitoring;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public interface IMonitoringQueries
{
    Task<MonitoringSummaryDto> GetSummaryAsync(CancellationToken cancellationToken);

    Task<DeviceMonitoringDto> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    Task<PagedResult<ConnectionEventDto>> ListEventsAsync(ConnectionEventQuery query, CancellationToken cancellationToken);

    Task<AvailabilityReportDto> GetAvailabilityAsync(AvailabilityReportQuery query, CancellationToken cancellationToken);

    Task<string> ExportAvailabilityCsvAsync(AvailabilityReportQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Read side of monitoring. Dashboards show the PERSISTED connectivity state
/// (the one alerts and history are based on), so a device is never displayed
/// offline before the monitor has established that the timeout was exceeded.
/// </summary>
internal sealed class MonitoringQueries(
    ISolvixDbContext db,
    IOrganizationDirectory organization,
    MonitoringSettingsProvider settingsProvider,
    IClock clock) : IMonitoringQueries
{
    public const int MaxReportDays = 366;

    public async Task<MonitoringSummaryDto> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var settings = await settingsProvider.GetAsync(cancellationToken);
        var rows = await (
                from p in db.Set<DevicePresence>().AsNoTracking()
                join d in db.Set<Device>().AsNoTracking() on p.Id equals d.Id
                where d.Status == DeviceStatus.Active
                select new FleetRow(p.Id, p.State, p.NetworkStatus, p.OsHealthStatus, d.BranchId, d.OsName, p.AccountedUntilUtc))
            .ToListAsync(cancellationToken);
        var retired = await db.Set<Device>().CountAsync(d => d.Status == DeviceStatus.Retired, cancellationToken);

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var availability = await ComputeAsync(today.AddDays(-29), today, now, rows.Select(r => new OpenInterval(r.Id, r.AccountedUntilUtc, r.State)).ToList(), cancellationToken);
        var last7 = await ComputeAsync(today.AddDays(-6), today, now, rows.Select(r => new OpenInterval(r.Id, r.AccountedUntilUtc, r.State)).ToList(), cancellationToken);

        var branchIds = rows.Where(r => r.BranchId is not null).Select(r => r.BranchId!.Value).Distinct().ToList();
        var branches = branchIds.Count == 0 ? new Dictionary<Guid, BranchSummary>() : await organization.FindBranchesAsync(branchIds, cancellationToken);
        var byBranch = rows
            .GroupBy(r => r.BranchId)
            .Select(g =>
            {
                var branch = g.Key is { } id && branches.TryGetValue(id, out var b) ? b : null;
                var totals = Sum(g.Select(r => last7.PerDevice.GetValueOrDefault(r.Id)));
                return new BranchMonitoringDto(g.Key, branch?.Name ?? (g.Key is null ? "Unassigned" : "Unknown branch"), branch?.Code, StateCountsDto.From(g.Select(r => r.State)), totals.AvailabilityPercent);
            })
            .OrderBy(b => b.BranchId is null ? 1 : 0).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var byOs = rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.OsName) ? "Unknown" : r.OsName!)
            .Select(g => new OperatingSystemMonitoringDto(g.Key, StateCountsDto.From(g.Select(r => r.State))))
            .OrderByDescending(o => o.Devices.Total).ThenBy(o => o.OperatingSystem, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Signals of a disconnected agent are stale: they are reported as Unknown, never as healthy.
        var network = Enum.GetValues<NetworkStatus>().ToDictionary(s => s.ToString(), _ => 0);
        var os = Enum.GetValues<OsHealthStatus>().ToDictionary(s => s.ToString(), _ => 0);
        foreach (var row in rows)
        {
            var connected = row.State == ConnectivityState.Online;
            network[(connected ? row.Network : NetworkStatus.Unknown).ToString()]++;
            os[(connected ? row.Os : OsHealthStatus.Unknown).ToString()]++;
        }

        var events = await ListEventsAsync(new ConnectionEventQuery(null, null, null, null, null, null, 1, 15), cancellationToken);
        var openAlerts = await db.Set<MonitoringAlert>().CountAsync(a => a.Status == MonitoringAlertStatus.Open, cancellationToken);

        return new MonitoringSummaryDto
        {
            GeneratedAtUtc = now,
            Devices = StateCountsDto.From(rows.Select(r => r.State)),
            RetiredDevices = retired,
            Availability7dPercent = last7.Total.AvailabilityPercent,
            Availability30dPercent = availability.Total.AvailabilityPercent,
            Branches = byBranch,
            OperatingSystems = byOs,
            Signals = new SignalCountsDto(network, os),
            RecentEvents = events.Items,
            OpenAlerts = openAlerts,
            Settings = settings.ToDto(),
        };
    }

    public async Task<DeviceMonitoringDto> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var device = await db.Set<Device>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
            ?? throw new NotFoundException("Device", deviceId);
        var presence = await db.Set<DevicePresence>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == deviceId, cancellationToken)
            ?? throw new NotFoundException("No monitoring data has been recorded for this device yet.");
        var settings = await settingsProvider.GetAsync(cancellationToken);
        var monitored = device.Status == DeviceStatus.Active;
        var connected = monitored && presence.State == ConnectivityState.Online;

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var open = monitored ? new List<OpenInterval> { new(deviceId, presence.AccountedUntilUtc, presence.State) } : [];
        var d1 = await ComputeAsync(today, today, now, open, cancellationToken, deviceId);
        var d7 = await ComputeAsync(today.AddDays(-6), today, now, open, cancellationToken, deviceId);
        var d30 = await ComputeAsync(today.AddDays(-29), today, now, open, cancellationToken, deviceId);

        var alert = await db.Set<MonitoringAlert>().AsNoTracking()
            .Where(a => a.DeviceId == deviceId && a.Status == MonitoringAlertStatus.Open)
            .OrderByDescending(a => a.OpenedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var events = await ListEventsAsync(new ConnectionEventQuery(deviceId, null, null, null, null, null, 1, 50), cancellationToken);
        var network = Read<NetworkReachabilitySnapshot>(presence.NetworkJson);
        var system = Read<SystemHealthSnapshot>(presence.OsHealthJson);

        return new DeviceMonitoringDto
        {
            DeviceId = deviceId,
            DeviceName = device.DisplayName ?? device.Hostname,
            Monitored = monitored,
            Connectivity = presence.State,
            StateChangedAtUtc = presence.StateChangedAtUtc,
            LastSeenAtUtc = presence.LastSeenAtUtc,
            LastCommunicationAtUtc = presence.LastCommunicationAtUtc,
            LastHeartbeatAtUtc = presence.LastHeartbeatAtUtc,
            OnlineSinceUtc = presence.OnlineSinceUtc,
            CurrentUptimeSeconds = presence.OnlineSinceUtc is { } since ? Seconds(now - since) : null,
            OfflineSinceUtc = presence.OfflineSinceUtc,
            CurrentDowntimeSeconds = presence.OfflineSinceUtc is { } down ? Seconds(now - down) : null,
            AgentStartedAtUtc = presence.AgentStartedAtUtc,
            SystemBootedAtUtc = presence.SystemBootedAtUtc,
            SystemUptimeSeconds = presence.SystemBootedAtUtc is { } boot ? Seconds((connected ? now : presence.LastSeenAtUtc) - boot) : null,
            Network = new SignalDto<NetworkStatus>(
                presence.NetworkStatus,
                presence.NetworkCheckedAtUtc,
                !connected,
                network?.Checks.Where(c => !c.Success).Select(c => $"{c.Kind} {c.Target}: {c.Error ?? "failed"}").ToList() ?? []),
            NetworkSnapshot = network,
            OperatingSystem = new SignalDto<OsHealthStatus>(presence.OsHealthStatus, presence.OsHealthCheckedAtUtc, !connected, PresencePolicy.DescribeOsIssues(system).ToList()),
            OperatingSystemSnapshot = system,
            Today = d1.Total,
            Last7Days = d7.Total,
            Last30Days = d30.Total,
            OpenAlert = alert is null ? null : MonitoringAlertDto.From(alert),
            RecentEvents = events.Items,
            Thresholds = settings.ToDto(),
        };
    }

    public async Task<PagedResult<ConnectionEventDto>> ListEventsAsync(ConnectionEventQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var events = db.Set<DeviceConnectionEvent>().AsNoTracking();
        if (query.DeviceId is { } deviceId)
        {
            events = events.Where(e => e.DeviceId == deviceId);
        }

        if (query.BranchId is { } branchId)
        {
            events = events.Where(e => db.Set<Device>().Any(d => d.Id == e.DeviceId && d.BranchId == branchId));
        }

        if (query.Type is { } type)
        {
            events = events.Where(e => e.Type == type);
        }

        if (query.Signal is { } signal)
        {
            events = events.Where(e => e.Signal == signal);
        }

        if (query.From is { } from)
        {
            events = events.Where(e => e.OccurredAtUtc >= from);
        }

        if (query.To is { } to)
        {
            events = events.Where(e => e.OccurredAtUtc < to);
        }

        var total = await events.LongCountAsync(cancellationToken);
        var items = await (
                from e in events
                join d in db.Set<Device>() on e.DeviceId equals d.Id
                orderby e.OccurredAtUtc descending, e.Id descending
                select new { Event = e, Name = d.DisplayName ?? d.Hostname })
            .Skip(page.Skip).Take(page.SafePageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<ConnectionEventDto>(items.Select(i => ToDto(i.Event, i.Name)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<AvailabilityReportDto> GetAvailabilityAsync(AvailabilityReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(-6);
        if (from > to)
        {
            throw new ValidationException("from", "The start date must not be after the end date.");
        }

        if (to.DayNumber - from.DayNumber + 1 > MaxReportDays)
        {
            throw new ValidationException("from", $"A report can cover at most {MaxReportDays} days.");
        }

        var devices = db.Set<Device>().AsNoTracking();
        if (query.BranchId is { } branchId)
        {
            devices = devices.Where(d => d.BranchId == branchId);
        }

        var deviceRows = await (
                from d in devices
                join p in db.Set<DevicePresence>().AsNoTracking() on d.Id equals p.Id
                select new { d.Id, Name = d.DisplayName ?? d.Hostname, d.BranchId, d.OsName, d.Status, p.AccountedUntilUtc, p.State })
            .ToListAsync(cancellationToken);
        var open = deviceRows.Where(d => d.Status == DeviceStatus.Active).Select(d => new OpenInterval(d.Id, d.AccountedUntilUtc, d.State)).ToList();
        var ids = deviceRows.Select(d => d.Id).ToList();
        var computed = await ComputeAsync(from, to, now, open, cancellationToken, deviceIds: ids);

        var branchIds = deviceRows.Where(d => d.BranchId is not null).Select(d => d.BranchId!.Value).Distinct().ToList();
        var branches = branchIds.Count == 0 ? new Dictionary<Guid, BranchSummary>() : await organization.FindBranchesAsync(branchIds, cancellationToken);
        string? BranchName(Guid? id) => id is { } b ? branches.TryGetValue(b, out var s) ? s.Name : "Unknown branch" : null;

        var rows = query.GroupBy == AvailabilityGrouping.Branch
            ? deviceRows
                .GroupBy(d => d.BranchId)
                .Select(g => new AvailabilityRowDto(g.Key ?? Guid.Empty, BranchName(g.Key) ?? "Unassigned", null, null, Sum(g.Select(d => computed.PerDevice.GetValueOrDefault(d.Id)))))
                .OrderBy(r => r.Totals.AvailabilityPercent ?? 101).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : deviceRows
                .Where(d => computed.PerDevice.ContainsKey(d.Id))
                .Select(d => new AvailabilityRowDto(d.Id, d.Name, BranchName(d.BranchId), d.OsName, computed.PerDevice[d.Id].ToDto()))
                .OrderBy(r => r.Totals.AvailabilityPercent ?? 101).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(i => from.AddDays(i))
            .Select(day => new AvailabilityDayDto(day, (computed.PerDay.GetValueOrDefault(day) ?? new Totals()).ToDto()))
            .ToList();
        return new AvailabilityReportDto(from, to, now, query.GroupBy, computed.Total, rows, days);
    }

    public async Task<string> ExportAvailabilityCsvAsync(AvailabilityReportQuery query, CancellationToken cancellationToken)
    {
        var report = await GetAvailabilityAsync(query, cancellationToken);
        var csv = new StringBuilder();
        csv.AppendLine(report.GroupBy == AvailabilityGrouping.Branch
            ? "branch,availability_percent,uptime_seconds,downtime_seconds,online_seconds,warning_seconds,offline_seconds,offline_count"
            : "device,branch,operating_system,availability_percent,uptime_seconds,downtime_seconds,online_seconds,warning_seconds,offline_seconds,offline_count");
        foreach (var row in report.Rows)
        {
            var t = row.Totals;
            var numbers = string.Join(',', Format(t.AvailabilityPercent), t.UptimeSeconds, t.DowntimeSeconds, t.OnlineSeconds, t.WarningSeconds, t.OfflineSeconds, t.OfflineCount);
            csv.AppendLine(report.GroupBy == AvailabilityGrouping.Branch
                ? $"{Escape(row.Name)},{numbers}"
                : $"{Escape(row.Name)},{Escape(row.BranchName)},{Escape(row.OperatingSystem)},{numbers}");
        }

        return csv.ToString();

        static string Format(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    internal static ConnectionEventDto ToDto(DeviceConnectionEvent e, string name) =>
        new(e.Id, e.DeviceId, name, e.Type, e.Signal, e.OccurredAtUtc, e.FromStatus, e.ToStatus, e.Detail, e.LastSeenAtUtc, e.DurationSeconds, e.FailedAttempts);

    /// <summary>
    /// Availability over [from, to] (UTC days): closed days from the daily ledger
    /// plus each device's still-open interval (since its last ledger write),
    /// attributed to its current state.
    /// </summary>
    private async Task<Computed> ComputeAsync(DateOnly from, DateOnly to, DateTimeOffset now, IReadOnlyList<OpenInterval> open, CancellationToken ct, Guid? deviceId = null, IReadOnlyList<Guid>? deviceIds = null)
    {
        var days = db.Set<DeviceAvailabilityDay>().AsNoTracking().Where(a => a.Day >= from && a.Day <= to);
        if (deviceId is { } single)
        {
            days = days.Where(a => a.DeviceId == single);
        }
        else if (deviceIds is not null)
        {
            days = days.Where(a => deviceIds.Contains(a.DeviceId));
        }

        var activeIds = db.Set<Device>().Where(d => d.Status == DeviceStatus.Active).Select(d => d.Id);
        if (deviceId is null && deviceIds is null)
        {
            days = days.Where(a => activeIds.Contains(a.DeviceId));
        }

        var ledger = await days
            .Select(a => new { a.DeviceId, a.Day, a.OnlineSeconds, a.WarningSeconds, a.OfflineSeconds, a.OfflineCount })
            .ToListAsync(ct);

        var result = new Computed();
        foreach (var row in ledger)
        {
            result.Add(row.DeviceId, row.Day, ConnectivityState.Online, row.OnlineSeconds);
            result.Add(row.DeviceId, row.Day, ConnectivityState.Warning, row.WarningSeconds);
            result.Add(row.DeviceId, row.Day, ConnectivityState.Offline, row.OfflineSeconds);
            result.AddOfflineCount(row.DeviceId, row.Day, row.OfflineCount);
        }

        var windowStart = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var windowEnd = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        foreach (var interval in open)
        {
            var start = interval.AccountedUntilUtc > windowStart ? interval.AccountedUntilUtc : windowStart;
            var end = now < windowEnd ? now : windowEnd;
            foreach (var (day, seconds) in PresencePolicy.SplitByDay(start, end))
            {
                result.Add(interval.DeviceId, day, interval.State, seconds);
            }
        }

        return result;
    }

    private static AvailabilityTotalsDto Sum(IEnumerable<Totals?> totals)
    {
        var sum = new Totals();
        foreach (var t in totals)
        {
            if (t is not null)
            {
                sum.Add(t);
            }
        }

        return sum.ToDto();
    }

    private static T? Read<T>(string? json)
        where T : class
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, DevicesJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Neutralise spreadsheet formula injection and quote per RFC 4180.
        var safe = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return safe.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : safe;
    }

    private static long Seconds(TimeSpan span) => Math.Max(0, (long)span.TotalSeconds);

    private sealed record FleetRow(Guid Id, ConnectivityState State, NetworkStatus Network, OsHealthStatus Os, Guid? BranchId, string? OsName, DateTimeOffset AccountedUntilUtc);

    private sealed record OpenInterval(Guid DeviceId, DateTimeOffset AccountedUntilUtc, ConnectivityState State);

    private sealed class Totals
    {
        public long Online { get; set; }

        public long Warning { get; set; }

        public long Offline { get; set; }

        public int OfflineCount { get; set; }

        public void Add(ConnectivityState state, long seconds)
        {
            switch (state)
            {
                case ConnectivityState.Online: Online += seconds; break;
                case ConnectivityState.Warning: Warning += seconds; break;
                default: Offline += seconds; break;
            }
        }

        public void Add(Totals other)
        {
            Online += other.Online;
            Warning += other.Warning;
            Offline += other.Offline;
            OfflineCount += other.OfflineCount;
        }

        public AvailabilityTotalsDto ToDto() =>
            new(Online, Warning, Offline, OfflineCount, PresencePolicy.AvailabilityPercent(Online, Warning, Offline));
    }

    private sealed class Computed
    {
        private readonly Totals _total = new();

        public Dictionary<Guid, Totals> PerDeviceTotals { get; } = [];

        public Dictionary<DateOnly, Totals> PerDay { get; } = [];

        public IReadOnlyDictionary<Guid, Totals> PerDevice => PerDeviceTotals;

        public AvailabilityTotalsDto Total => _total.ToDto();

        public void Add(Guid deviceId, DateOnly day, ConnectivityState state, long seconds)
        {
            if (seconds <= 0)
            {
                return;
            }

            Get(PerDeviceTotals, deviceId).Add(state, seconds);
            Get(PerDay, day).Add(state, seconds);
            _total.Add(state, seconds);
        }

        public void AddOfflineCount(Guid deviceId, DateOnly day, int count)
        {
            if (count <= 0)
            {
                return;
            }

            Get(PerDeviceTotals, deviceId).OfflineCount += count;
            Get(PerDay, day).OfflineCount += count;
            _total.OfflineCount += count;
        }

        private static Totals Get<TKey>(Dictionary<TKey, Totals> map, TKey key)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out var totals))
            {
                totals = new Totals();
                map[key] = totals;
            }

            return totals;
        }
    }
}
