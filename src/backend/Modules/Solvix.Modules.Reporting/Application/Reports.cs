namespace Solvix.Modules.Reporting.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application.Agent;
using Solvix.Modules.Devices.Configuration;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Monitoring;
using Solvix.Modules.Helpdesk.Domain;
using Solvix.Modules.Reporting.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Directory;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;

internal static class ReportColumns
{
    public static ReportColumn S(string key, string label) => new(key, label, "string");

    public static ReportColumn N(string key, string label) => new(key, label, "number");

    public static ReportColumn T(string key, string label) => new(key, label, "datetime");

    public static ReportColumn D(string key, string label) => new(key, label, "date");

    public static ReportColumn B(string key, string label) => new(key, label, "bool");
}

internal abstract class ReportBase(ISolvixDbContext db, IClock clock)
{
    protected ISolvixDbContext Db => db;

    protected DateTimeOffset Now => clock.UtcNow;

    protected static IReadOnlyList<object?> Row(params object?[] values) => values;

    protected ReportResult Build(string key, string title, IReadOnlyList<ReportMetric> summary, IReadOnlyList<ReportColumn> columns, List<IReadOnlyList<object?>> rows, string? notes = null)
    {
        var truncated = rows.Count > ReportLimits.MaxRows;
        return new ReportResult(key, title, Now, summary, columns, truncated ? rows.Take(ReportLimits.MaxRows).ToList() : rows, truncated, notes);
    }

    /// <summary>Connectivity as tracked by live monitoring (Phase 04): last contact and Online/Warning/Offline state.</summary>
    protected async Task<Dictionary<Guid, DevicePresence>> PresenceAsync(IReadOnlyCollection<Device> devices, CancellationToken cancellationToken)
    {
        var ids = devices.Select(d => d.Id).ToList();
        return await db.Set<DevicePresence>().AsNoTracking().Where(p => ids.Contains(p.DeviceId)).ToDictionaryAsync(p => p.DeviceId, cancellationToken);
    }

    /// <summary>Online, including "Warning" (heartbeat late but within the offline threshold).</summary>
    protected static bool IsOnline(Dictionary<Guid, DevicePresence> presence, Device device) =>
        presence.TryGetValue(device.Id, out var p) && p.State is ConnectivityState.Online or ConnectivityState.Warning;

    protected static DateTimeOffset? LastSeen(Dictionary<Guid, DevicePresence> presence, Device device) =>
        presence.TryGetValue(device.Id, out var p) ? p.LastSeenAtUtc : null;

    protected IQueryable<Device> Devices(ReportQuery query)
    {
        var devices = db.Set<Device>().AsNoTracking();
        if (query.BranchId is { } branch)
        {
            devices = devices.Where(d => d.BranchId == branch);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = "%" + query.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            devices = devices.Where(d => EF.Functions.ILike(d.Hostname, pattern) || (d.SerialNumber != null && EF.Functions.ILike(d.SerialNumber, pattern)));
        }

        return devices;
    }

    protected static (int Revision, bool Valid) Revision(string? build)
    {
        // "22631.4460" -> 4460. Builds without a revision are not comparable.
        var dot = build?.LastIndexOf('.') ?? -1;
        return dot > 0 && int.TryParse(build![(dot + 1)..], out var rev) ? (rev, true) : (0, false);
    }

    protected static string BuildOf(string? build)
    {
        var dot = build?.LastIndexOf('.') ?? -1;
        return dot > 0 ? build![..dot] : build ?? string.Empty;
    }
}

// ------------------------------------------------------------------------------------------------------------------
internal sealed class DeviceInventoryReport(ISolvixDbContext db, IClock clock, IOrganizationDirectory organization) : ReportBase(db, clock), IReport
{
    public string Key => "device-inventory";

    public string Title => "Device inventory";

    public string Description => "Every enrolled device with hardware, operating system, network and assignment.";

    public IReadOnlyList<string> Filters => ["branchId", "search"];

    public async Task<ReportResult> RunAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var list = await Devices(query).OrderBy(d => d.Hostname).Take(ReportLimits.MaxRows + 1).ToListAsync(cancellationToken);
        var presence = await PresenceAsync(list, cancellationToken);
        var branches = await organization.FindBranchesAsync(list.Where(d => d.BranchId != null).Select(d => d.BranchId!.Value).Distinct().ToList(), cancellationToken);
        var departments = await organization.FindDepartmentsAsync(list.Where(d => d.DepartmentId != null).Select(d => d.DepartmentId!.Value).Distinct().ToList(), cancellationToken);

        var rows = list.Select(d => Row(
            d.Hostname, d.DisplayName, d.Status.ToString(),
            d.Status == DeviceStatus.Active && IsOnline(presence, d) ? "Online" : "Offline",
            d.OsName, d.OsDisplayVersion, d.OsBuild, d.Manufacturer, d.Model, d.SerialNumber, d.ProcessorName,
            d.TotalMemoryBytes is { } m ? Math.Round(m / 1_073_741_824d, 1) : (double?)null,
            string.Join(' ', d.IpAddresses), string.Join(' ', d.MacAddresses),
            d.BranchId is { } b && branches.TryGetValue(b, out var bn) ? bn.Name : null,
            d.DepartmentId is { } dp && departments.TryGetValue(dp, out var dn) ? dn.Name : null,
            d.AgentVersion, LastSeen(presence, d), d.EnrolledAtUtc)).ToList();

        var active = list.Where(d => d.Status == DeviceStatus.Active).ToList();
        var summary = new List<ReportMetric>
        {
            new("Devices", list.Count),
            new("Active", active.Count),
            new("Retired", list.Count - active.Count),
            new("Online now", active.Count(d => IsOnline(presence, d))),
        };
        summary.AddRange(active.GroupBy(d => d.OsName ?? "Unknown").OrderByDescending(g => g.Count()).Take(5).Select(g => new ReportMetric($"OS: {g.Key}", g.Count())));
        return Build(Key, Title, summary,
            [ReportColumns.S("hostname", "Hostname"), ReportColumns.S("displayName", "Display name"), ReportColumns.S("status", "Status"), ReportColumns.S("connectivity", "Connectivity"),
             ReportColumns.S("os", "Operating system"), ReportColumns.S("osVersion", "OS version"), ReportColumns.S("osBuild", "OS build"), ReportColumns.S("manufacturer", "Manufacturer"),
             ReportColumns.S("model", "Model"), ReportColumns.S("serial", "Serial number"), ReportColumns.S("cpu", "Processor"), ReportColumns.N("ramGb", "RAM (GB)"),
             ReportColumns.S("ip", "IP addresses"), ReportColumns.S("mac", "MAC addresses"), ReportColumns.S("branch", "Branch"), ReportColumns.S("department", "Department"),
             ReportColumns.S("agent", "Agent version"), ReportColumns.T("lastSeen", "Last seen"), ReportColumns.T("enrolled", "Enrolled")],
            rows);
    }
}

// ------------------------------------------------------------------------------------------------------------------
internal sealed class DeviceAvailabilityReport(ISolvixDbContext db, IClock clock) : ReportBase(db, clock), IReport
{
    public string Key => "device-availability";

    public string Title => "Device availability";

    public string Description => "Who is online now, who has been silent and for how long, and the fleet availability trend.";

    public IReadOnlyList<string> Filters => ["branchId", "days", "search"];

    public async Task<ReportResult> RunAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var days = Math.Clamp(query.Days ?? 30, 1, 365);
        var now = Now;
        var loaded = await Devices(query).Where(d => d.Status == DeviceStatus.Active).OrderBy(d => d.Hostname).Take(ReportLimits.MaxRows + 1).ToListAsync(cancellationToken);
        var presence = await PresenceAsync(loaded, cancellationToken);
        var list = loaded.OrderBy(d => LastSeen(presence, d) ?? DateTimeOffset.MinValue).ThenBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase).ToList();

        var since = now.AddDays(-days);
        var samples = await Db.Set<AvailabilitySnapshot>().AsNoTracking().Where(s => s.HourUtc >= since && s.ActiveDevices > 0)
            .Select(s => new { s.ActiveDevices, s.OnlineDevices }).ToListAsync(cancellationToken);
        double? trend = samples.Count == 0 ? null : Math.Round(samples.Sum(s => (double)s.OnlineDevices) / samples.Sum(s => (double)s.ActiveDevices) * 100, 2);

        var online = list.Count(d => IsOnline(presence, d));
        string Bucket(Device d) => IsOnline(presence, d) ? "Online" : LastSeen(presence, d) is not { } seen ? "Never seen" : (now - seen) switch
        {
            var age when age.TotalHours <= 1 => "Offline < 1 hour",
            var age when age.TotalHours <= 24 => "Offline < 24 hours",
            var age when age.TotalDays <= 7 => "Offline < 7 days",
            _ => "Offline > 7 days",
        };

        var rows = list.Select(d => Row(
            d.Hostname, Bucket(d), LastSeen(presence, d),
            !IsOnline(presence, d) && LastSeen(presence, d) is { } s ? Math.Round((now - s).TotalHours, 1) : (double?)null,
            d.LastBootAtUtc is { } b ? Math.Round((now - b).TotalDays, 1) : (double?)null,
            d.AgentVersion, d.HealthStatus?.ToString())).ToList();

        var summary = new List<ReportMetric>
        {
            new("Active devices", list.Count),
            new("Online now", online),
            new("Offline now", list.Count - online),
            new("Availability now (%)", list.Count == 0 ? null : Math.Round(online * 100d / list.Count, 1)),
            new($"Average availability, last {days} days (%)", trend),
            new("Trend samples (hourly)", samples.Count),
        };
        summary.AddRange(list.GroupBy(d => Bucket(d)).OrderBy(g => g.Key).Select(g => new ReportMetric(g.Key, g.Count())));
        return Build(Key, Title, summary,
            [ReportColumns.S("hostname", "Hostname"), ReportColumns.S("bucket", "State"), ReportColumns.T("lastSeen", "Last seen"), ReportColumns.N("offlineHours", "Offline (hours)"),
             ReportColumns.N("daysSinceBoot", "Days since boot"), ReportColumns.S("agent", "Agent version"), ReportColumns.S("health", "Agent health")],
            rows,
            "The trend is computed from hourly samples taken while the platform is running; it starts when the platform first records a sample for the workspace.");
    }
}

// ------------------------------------------------------------------------------------------------------------------
internal sealed class AssetLifecycleReport(ISolvixDbContext db, IClock clock) : ReportBase(db, clock), IReport
{
    public string Key => "asset-lifecycle";

    public string Title => "Asset lifecycle";

    public string Description => "Age, warranty status, OS support and refresh candidates for every device.";

    public IReadOnlyList<string> Filters => ["branchId", "search"];

    public async Task<ReportResult> RunAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var now = Now;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var list = await Devices(query).OrderBy(d => d.Hostname).Take(ReportLimits.MaxRows + 1).ToListAsync(cancellationToken);
        var flags = await InventoryQueries.LoadFlagsAsync(Db, cancellationToken);

        string Warranty(Device d) => d.WarrantyExpiresOn is not { } w ? "Unknown" : w < today ? "Expired" : w <= today.AddDays(90) ? "Expiring within 90 days" : "In warranty";

        var rows = list.Select(d =>
        {
            flags.TryGetValue(d.Id, out var f);
            // Hardware age: purchase date if recorded, else BIOS release, else first enrollment (least reliable).
            var (basis, since) = d.PurchasedOn is { } p ? ("Purchase date", p.ToDateTime(TimeOnly.MinValue))
                : f?.BiosReleaseUtc is { } b ? ("BIOS release date", b.UtcDateTime)
                : ("Enrollment date", d.EnrolledAtUtc.UtcDateTime);
            var ageYears = Math.Round((now.UtcDateTime - since).TotalDays / 365.25, 1);
            var refresh = d.Status == DeviceStatus.Retired ? "Retired"
                : ageYears >= 5 || f?.OsSupported == false || Warranty(d) == "Expired" && ageYears >= 3 ? "Refresh candidate" : "OK";
            return Row(d.Hostname, d.Status.ToString(), d.Manufacturer, d.Model, d.SerialNumber, d.EnrolledAtUtc, d.PurchasedOn, d.WarrantyExpiresOn, Warranty(d),
                basis, ageYears, f?.OsSupported, d.RetiredAtUtc, refresh);
        }).ToList();

        var active = list.Where(d => d.Status == DeviceStatus.Active).ToList();
        var summary = new List<ReportMetric>
        {
            new("Devices", list.Count),
            new("Warranty expired", list.Count(d => d.Status == DeviceStatus.Active && Warranty(d) == "Expired")),
            new("Warranty expiring within 90 days", list.Count(d => d.Status == DeviceStatus.Active && Warranty(d) == "Expiring within 90 days")),
            new("Warranty unknown", list.Count(d => d.Status == DeviceStatus.Active && Warranty(d) == "Unknown")),
            new("Refresh candidates", rows.Count(r => (string?)r[13] == "Refresh candidate")),
            new("Retired", list.Count - active.Count),
        };
        return Build(Key, Title, summary,
            [ReportColumns.S("hostname", "Hostname"), ReportColumns.S("status", "Status"), ReportColumns.S("manufacturer", "Manufacturer"), ReportColumns.S("model", "Model"),
             ReportColumns.S("serial", "Serial number"), ReportColumns.T("enrolled", "Enrolled"), ReportColumns.D("purchasedOn", "Purchased"), ReportColumns.D("warrantyExpiresOn", "Warranty ends"),
             ReportColumns.S("warranty", "Warranty status"), ReportColumns.S("ageBasis", "Age based on"), ReportColumns.N("ageYears", "Age (years)"), ReportColumns.B("osSupported", "OS supported"),
             ReportColumns.T("retired", "Retired"), ReportColumns.S("recommendation", "Recommendation")],
            rows,
            "Purchase and warranty dates are entered by administrators (Devices > lifecycle). Where they are missing, age falls back to the BIOS release date, then the enrollment date. Refresh candidate = 5+ years old, unsupported OS, or out of warranty and 3+ years old.");
    }
}

// ------------------------------------------------------------------------------------------------------------------
internal sealed class SoftwareInventoryReport(ISolvixDbContext db, IClock clock) : ReportBase(db, clock), IReport
{
    public string Key => "software-inventory";

    public string Title => "Software inventory";

    public string Description => "Installed applications across the fleet, grouped by name, version and publisher.";

    public IReadOnlyList<string> Filters => ["search"];

    private sealed class Row2
    {
        public string Name { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Publisher { get; set; } = string.Empty;

        public long Devices { get; set; }
    }

    public async Task<ReportResult> RunAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var pattern = string.IsNullOrWhiteSpace(query.Search)
            ? "%"
            : "%" + query.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var items = await Db.Database.SqlQuery<Row2>($"""
            SELECT s ->> 'name' AS "Name", COALESCE(s ->> 'version', '') AS "Version", COALESCE(s ->> 'publisher', '') AS "Publisher",
                   count(DISTINCT i.device_id) AS "Devices"
            FROM device_inventories i
            JOIN devices d ON d.id = i.device_id AND d.status = 'Active'
            CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(i.report_json -> 'software') = 'array' THEN i.report_json -> 'software' ELSE '[]'::jsonb END) s
            WHERE s ->> 'name' IS NOT NULL AND s ->> 'name' ILIKE {pattern}
            GROUP BY 1, 2, 3
            ORDER BY 4 DESC, 1, 2
            LIMIT {ReportLimits.MaxRows + 1}
            """).ToListAsync(cancellationToken);

        var reporting = await Db.Set<DeviceInventory>().AsNoTracking().CountAsync(cancellationToken);
        var rows = items.Select(i => Row(i.Name, i.Version, i.Publisher, i.Devices)).ToList();
        var summary = new List<ReportMetric>
        {
            new("Devices with an inventory", reporting),
            new("Distinct name/version entries", items.Count),
            new("Distinct applications", items.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()),
        };
        return Build(Key, Title, summary,
            [ReportColumns.S("name", "Application"), ReportColumns.S("version", "Version"), ReportColumns.S("publisher", "Publisher"), ReportColumns.N("devices", "Devices")],
            rows);
    }
}

// ------------------------------------------------------------------------------------------------------------------
internal sealed class PatchComplianceReport(ISolvixDbContext db, IClock clock) : ReportBase(db, clock), IReport
{
    public const int StaleInventoryDays = 14;
    public const int RebootRiskDays = 30;

    public string Key => "patch-compliance";

    public string Title => "Patch compliance";

    public string Description => "OS update level of each device against the newest build seen in your fleet, plus unsupported systems and long uptimes.";

    public IReadOnlyList<string> Filters => ["branchId", "search"];

    public async Task<ReportResult> RunAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var now = Now;
        var list = await Devices(query).Where(d => d.Status == DeviceStatus.Active).OrderBy(d => d.Hostname).Take(ReportLimits.MaxRows + 1).ToListAsync(cancellationToken);
        var flags = await InventoryQueries.LoadFlagsAsync(Db, cancellationToken);

        // Baseline = highest revision observed per (OS name, OS release, build) across the WHOLE workspace, not just the filter.
        var all = await Db.Set<Device>().AsNoTracking().Where(d => d.Status == DeviceStatus.Active).Select(d => new { d.OsName, d.OsDisplayVersion, d.OsBuild }).ToListAsync(cancellationToken);
        var baseline = all
            .Select(d => (Key: $"{d.OsName}|{d.OsDisplayVersion}|{BuildOf(d.OsBuild)}", Rev: Revision(d.OsBuild)))
            .Where(x => x.Rev.Valid)
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Rev.Revision));

        var rows = new List<IReadOnlyList<object?>>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in list)
        {
            flags.TryGetValue(d.Id, out var f);
            var (rev, valid) = Revision(d.OsBuild);
            var key = $"{d.OsName}|{d.OsDisplayVersion}|{BuildOf(d.OsBuild)}";
            int? latest = baseline.TryGetValue(key, out var l) ? l : null;
            var daysSinceBoot = d.LastBootAtUtc is { } boot ? Math.Round((now - boot).TotalDays, 1) : (double?)null;
            var inventoryAge = d.LastInventoryAtUtc is { } li ? (now - li).TotalDays : double.MaxValue;

            string state;
            if (f?.OsSupported == false)
            {
                state = "Unsupported OS";
            }
            else if (!valid)
            {
                state = "Unknown";
            }
            else if (inventoryAge > StaleInventoryDays)
            {
                state = "Stale data";
            }
            else if (latest is { } lt && rev < lt)
            {
                state = "Behind";
            }
            else
            {
                state = "Current";
            }

            counts[state] = counts.GetValueOrDefault(state) + 1;
            rows.Add(Row(d.Hostname, d.OsName, d.OsDisplayVersion, d.OsBuild, valid ? rev : null, latest, valid && latest is { } l2 ? Math.Max(0, l2 - rev) : null,
                f?.OsSupported, daysSinceBoot, daysSinceBoot is { } ds && ds > RebootRiskDays, d.LastInventoryAtUtc, state));
        }

        var compliant = counts.GetValueOrDefault("Current");
        var summary = new List<ReportMetric>
        {
            new("Active devices", list.Count),
            new("Compliance (%)", list.Count == 0 ? null : Math.Round(compliant * 100d / list.Count, 1)),
            new("Current", compliant),
            new("Behind", counts.GetValueOrDefault("Behind")),
            new("Unsupported OS", counts.GetValueOrDefault("Unsupported OS")),
            new("Stale or unknown", counts.GetValueOrDefault("Stale data") + counts.GetValueOrDefault("Unknown")),
            new($"Up for more than {RebootRiskDays} days", rows.Count(r => r[9] is true)),
        };
        return Build(Key, Title, summary,
            [ReportColumns.S("hostname", "Hostname"), ReportColumns.S("os", "Operating system"), ReportColumns.S("release", "Release"), ReportColumns.S("build", "Build"),
             ReportColumns.N("revision", "Revision"), ReportColumns.N("latestRevision", "Newest in fleet"), ReportColumns.N("revisionsBehind", "Revisions behind"), ReportColumns.B("osSupported", "OS supported"),
             ReportColumns.N("daysSinceBoot", "Days since boot"), ReportColumns.B("rebootRisk", "Long uptime"), ReportColumns.T("lastInventory", "Last inventory"), ReportColumns.S("state", "State")],
            rows,
            "Derived from inventory: a device is Behind when its cumulative-update revision is lower than the newest revision of the same Windows release seen in this workspace. " +
            "This is a relative measure; the current agent does not read Windows Update history, so it cannot tell which specific KBs are missing. " +
            $"Stale data = no inventory for {StaleInventoryDays}+ days.");
    }
}

// ------------------------------------------------------------------------------------------------------------------
internal sealed class SecurityComplianceReport(ISolvixDbContext db, IClock clock, IAgentReleaseCatalog releases) : ReportBase(db, clock), IReport
{
    public string Key => "security-compliance";

    public string Title => "Security compliance";

    public string Description => "Per-device security posture: supported OS, Secure Boot, disk encryption, agent health and currency, recent contact.";

    public IReadOnlyList<string> Filters => ["branchId", "search"];

    public async Task<ReportResult> RunAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var now = Now;
        var list = await Devices(query).Where(d => d.Status == DeviceStatus.Active).OrderBy(d => d.Hostname).Take(ReportLimits.MaxRows + 1).ToListAsync(cancellationToken);
        var flags = await InventoryQueries.LoadFlagsAsync(Db, cancellationToken);
        var presence = await PresenceAsync(list, cancellationToken);

        var rows = new List<IReadOnlyList<object?>>();
        var failures = new[] { 0, 0, 0, 0, 0, 0 };
        var compliant = 0;
        foreach (var d in list)
        {
            flags.TryGetValue(d.Id, out var f);
            bool? osOk = f?.OsSupported;
            bool? secureBoot = f?.SecureBoot;
            bool? encrypted = f?.AllFixedVolumesEncrypted;
            bool? health = d.HealthStatus is null ? null : d.HealthStatus == AgentHealthStatus.Healthy;
            var agentCurrent = !releases.IsUpdateAvailable(d.AgentVersion);
            var recent = LastSeen(presence, d) is { } seen && now - seen <= TimeSpan.FromDays(7);

            var checks = new bool?[] { osOk, secureBoot, encrypted, health, agentCurrent, recent };
            for (var i = 0; i < checks.Length; i++)
            {
                if (checks[i] == false)
                {
                    failures[i]++;
                }
            }

            var failed = checks.Count(c => c == false);
            var unknown = checks.Count(c => c is null);
            var result = failed > 0 ? "Non-compliant" : unknown > 0 ? "Unknown" : "Compliant";
            if (result == "Compliant")
            {
                compliant++;
            }

            rows.Add(Row(d.Hostname, osOk, secureBoot, encrypted, health, agentCurrent, recent, failed, result));
        }

        var summary = new List<ReportMetric>
        {
            new("Active devices", list.Count),
            new("Fully compliant (%)", list.Count == 0 ? null : Math.Round(compliant * 100d / list.Count, 1)),
            new("Unsupported OS", failures[0]),
            new("Secure Boot off", failures[1]),
            new("Disk not fully encrypted", failures[2]),
            new("Agent unhealthy", failures[3]),
            new("Agent out of date", failures[4]),
            new("Not seen for 7+ days", failures[5]),
        };
        return Build(Key, Title, summary,
            [ReportColumns.S("hostname", "Hostname"), ReportColumns.B("osSupported", "OS supported"), ReportColumns.B("secureBoot", "Secure Boot"), ReportColumns.B("diskEncrypted", "Fixed disks BitLocker-protected"),
             ReportColumns.B("agentHealthy", "Agent healthy"), ReportColumns.B("agentCurrent", "Agent up to date"), ReportColumns.B("recentContact", "Seen in last 7 days"), ReportColumns.N("failedChecks", "Failed checks"), ReportColumns.S("result", "Result")],
            rows,
            "Blank means the agent has not reported that fact (for example no inventory yet); such devices are 'Unknown', not compliant. The agent does not currently report firewall or antivirus state, so those are not assessed.");
    }
}

// ------------------------------------------------------------------------------------------------------------------
internal sealed class DeploymentReport(ISolvixDbContext db, IClock clock, IAgentReleaseCatalog releases) : ReportBase(db, clock), IReport
{
    public string Key => "deployment";

    public string Title => "Agent deployment";

    public string Description => "Agent rollout: enrollment activity, version spread, devices pending an update and enrollment token usage.";

    public IReadOnlyList<string> Filters => ["branchId", "days", "search"];

    public async Task<ReportResult> RunAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var days = Math.Clamp(query.Days ?? 30, 1, 365);
        var now = Now;
        var since = now.AddDays(-days);
        var list = await Devices(query).Where(d => d.Status == DeviceStatus.Active).OrderByDescending(d => d.EnrolledAtUtc).Take(ReportLimits.MaxRows + 1).ToListAsync(cancellationToken);
        var presence = await PresenceAsync(list, cancellationToken);

        var tokens = await Db.Set<EnrollmentToken>().AsNoTracking().Where(t => t.CreatedAtUtc >= since).ToListAsync(cancellationToken);
        var latest = releases.LatestVersion;

        var rows = list.Select(d => Row(
            d.Hostname, d.EnrolledAtUtc, d.AgentVersion, d.AgentVersionChangedAtUtc, releases.IsUpdateAvailable(d.AgentVersion), d.HealthStatus?.ToString(), LastSeen(presence, d), d.EnrollmentCount)).ToList();

        var summary = new List<ReportMetric>
        {
            new("Latest agent release", latest),
            new("Active devices", list.Count),
            new($"Enrolled in the last {days} days", list.Count(d => d.EnrolledAtUtc >= since)),
            new("Pending an agent update", list.Count(d => releases.IsUpdateAvailable(d.AgentVersion))),
            new("Re-enrolled devices", list.Count(d => d.EnrollmentCount > 1)),
            new($"Enrollment tokens created ({days} days)", tokens.Count),
            new("Token uses", tokens.Sum(t => t.UseCount)),
            new("Tokens revoked", tokens.Count(t => t.RevokedAtUtc != null)),
        };
        summary.AddRange(list.GroupBy(d => d.AgentVersion).OrderByDescending(g => g.Key).Take(8).Select(g => new ReportMetric($"Agent {g.Key}", g.Count())));
        return Build(Key, Title, summary,
            [ReportColumns.S("hostname", "Hostname"), ReportColumns.T("enrolled", "Enrolled"), ReportColumns.S("agent", "Agent version"), ReportColumns.T("versionChanged", "Version changed"),
             ReportColumns.B("updatePending", "Update pending"), ReportColumns.S("health", "Agent health"), ReportColumns.T("lastSeen", "Last seen"), ReportColumns.N("enrollments", "Enrollments")],
            rows,
            "Covers the Solvix agent rollout. Software or patch deployment jobs are not part of the current platform.");
    }
}

// ------------------------------------------------------------------------------------------------------------------
internal sealed class TechnicianActivityReport(ISolvixDbContext db, IClock clock, IMemberDirectory members) : ReportBase(db, clock), IReport
{
    public string Key => "technician-activity";

    public string Title => "Technician activity";

    public string Description => "Workload, resolutions, response and resolution times and SLA outcomes per technician.";

    public IReadOnlyList<string> Filters => ["days"];

    public async Task<ReportResult> RunAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var days = Math.Clamp(query.Days ?? 30, 1, 365);
        var now = Now;
        var since = now.AddDays(-days);

        var technicians = await Db.Set<Technician>().AsNoTracking().ToListAsync(cancellationToken);
        var names = await members.FindAsync(technicians.Select(t => t.UserId).ToList(), cancellationToken);

        var tickets = await Db.Set<Ticket>().AsNoTracking()
            .Where(t => t.AssigneeTechnicianId != null && (t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed || t.ResolvedAtUtc >= since || t.CreatedAtUtc >= since))
            .Select(t => new { Tech = t.AssigneeTechnicianId!.Value, t.Status, t.CreatedAtUtc, t.ResolvedAtUtc, t.FirstRespondedAtUtc, t.ResolutionBreached, t.FirstResponseBreached, t.ReopenCount })
            .ToListAsync(cancellationToken);
        var comments = await Db.Set<TicketComment>().AsNoTracking().Where(c => c.IsFromStaff && c.AuthorUserId != null && c.CreatedAtUtc >= since)
            .GroupBy(c => new { c.AuthorUserId, c.IsInternal }).Select(g => new { g.Key.AuthorUserId, g.Key.IsInternal, Count = g.Count() }).ToListAsync(cancellationToken);

        var rows = new List<IReadOnlyList<object?>>();
        foreach (var tech in technicians.OrderBy(t => names.TryGetValue(t.UserId, out var m) ? m.DisplayName : string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var mine = tickets.Where(t => t.Tech == tech.Id).ToList();
            var open = mine.Count(t => t.Status is not (TicketStatus.Resolved or TicketStatus.Closed));
            var resolved = mine.Where(t => t.ResolvedAtUtc >= since).ToList();
            var created = mine.Count(t => t.CreatedAtUtc >= since);
            var responseMinutes = mine.Where(t => t.FirstRespondedAtUtc != null && t.CreatedAtUtc >= since).Select(t => (t.FirstRespondedAtUtc!.Value - t.CreatedAtUtc).TotalMinutes).ToList();
            var resolutionHours = resolved.Select(t => (t.ResolvedAtUtc!.Value - t.CreatedAtUtc).TotalHours).ToList();
            var breaches = mine.Count(t => (t.ResolutionBreached || t.FirstResponseBreached) && (t.CreatedAtUtc >= since || t.ResolvedAtUtc >= since));
            var replies = comments.Where(c => c.AuthorUserId == tech.UserId && !c.IsInternal).Sum(c => c.Count);
            var notes = comments.Where(c => c.AuthorUserId == tech.UserId && c.IsInternal).Sum(c => c.Count);
            var slaMet = resolved.Count == 0 ? (double?)null : Math.Round(resolved.Count(t => !t.ResolutionBreached) * 100d / resolved.Count, 1);
            names.TryGetValue(tech.UserId, out var member);
            rows.Add(Row(member?.DisplayName ?? "(unknown)", member?.Email, tech.IsActive, open, created, resolved.Count,
                responseMinutes.Count == 0 ? null : Math.Round(responseMinutes.Average(), 1),
                resolutionHours.Count == 0 ? null : Math.Round(resolutionHours.Average(), 1),
                slaMet, breaches, replies, notes, resolved.Sum(t => t.ReopenCount)));
        }

        var summary = new List<ReportMetric>
        {
            new("Period (days)", days),
            new("Technicians", technicians.Count),
            new("Open tickets assigned", rows.Sum(r => (int)r[3]!)),
            new("Resolved in period", rows.Sum(r => (int)r[5]!)),
            new("SLA breaches in period", rows.Sum(r => (int)r[9]!)),
        };
        return Build(Key, Title, summary,
            [ReportColumns.S("technician", "Technician"), ReportColumns.S("email", "Email"), ReportColumns.B("active", "Active"), ReportColumns.N("open", "Open now"), ReportColumns.N("assigned", "Assigned in period"),
             ReportColumns.N("resolved", "Resolved in period"), ReportColumns.N("avgFirstResponseMin", "Avg first response (min)"), ReportColumns.N("avgResolutionHours", "Avg resolution (h)"),
             ReportColumns.N("slaMetPct", "Resolved within SLA (%)"), ReportColumns.N("breaches", "SLA breaches"), ReportColumns.N("replies", "Public replies"), ReportColumns.N("notes", "Internal notes"), ReportColumns.N("reopened", "Reopened")],
            rows);
    }
}
