namespace Solvix.Modules.Assistant.Demo;

using System.Security.Cryptography;
using System.Text;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Fleet;

/// <summary>
/// Deterministic, in-memory demo fleet (enabled with <c>Solvix:Assistant:UseDemoFleet</c>, refused in Production).
/// It spreads synthetic devices over the tenant's REAL branches and departments so the assistant can be tried
/// end-to-end before the device modules exist. It is a stand-in for <see cref="IFleetQueryService"/>, nothing more.
/// </summary>
internal sealed class DemoFleet(IBranchService branches, IDepartmentService departments, IClock clock) : IFleetQueryService
{
    private const long Gb = 1024L * 1024 * 1024;

    public bool IsAvailable => true;

    public async Task<FleetSummary> GetSummaryAsync(FleetAccess access, FleetScope scope, CancellationToken cancellationToken)
    {
        var devices = Filter(await BuildAsync(access.TenantId, cancellationToken), new DeviceSearchCriteria { BranchId = scope.BranchId, DepartmentIds = scope.DepartmentIds }).ToList();
        return new FleetSummary(
            devices.Count,
            devices.Count(d => d.Status == DeviceConnectivity.Online),
            devices.Count(d => d.Status == DeviceConnectivity.Offline),
            devices.Count(d => d.Status == DeviceConnectivity.Unknown),
            devices.Count(d => d.FreeBytes < 10 * Gb),
            devices.GroupBy(d => d.Os).ToDictionary(g => g.Key, g => g.Count()),
            devices.GroupBy(d => d.BranchName ?? "Unassigned")
                .Select(g => new BranchFleetCount(g.Key, g.Count(), g.Count(d => d.Status == DeviceConnectivity.Online), g.Count(d => d.Status == DeviceConnectivity.Offline)))
                .OrderBy(b => b.BranchName, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    public async Task<(IReadOnlyList<DeviceSummary> Items, long Total)> SearchAsync(FleetAccess access, DeviceSearchCriteria criteria, int skip, int take, CancellationToken cancellationToken)
    {
        var filtered = Filter(await BuildAsync(access.TenantId, cancellationToken), criteria);
        var sorted = criteria.Sort switch
        {
            DeviceSort.LastSeen => filtered.OrderBy(d => d.LastSeen),
            DeviceSort.CpuUsageDesc => filtered.OrderByDescending(d => d.Cpu),
            DeviceSort.MemoryUsageDesc => filtered.OrderByDescending(d => d.Memory),
            DeviceSort.FreeStorageAsc => filtered.OrderBy(d => d.FreeBytes),
            _ => filtered.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase),
        };
        var all = sorted.ToList();
        return (all.Skip(skip).Take(take).Select(d => d.Summary()).ToList(), all.Count);
    }

    public async Task<DeviceResolution> ResolveAsync(FleetAccess access, IReadOnlyList<string> references, CancellationToken cancellationToken)
    {
        var all = await BuildAsync(access.TenantId, cancellationToken);
        var found = new List<DeviceSummary>();
        var notFound = new List<string>();
        var ambiguous = new List<string>();
        foreach (var reference in references)
        {
            var byId = Guid.TryParse(reference, out var id) ? all.FirstOrDefault(d => d.Id == id) : null;
            if (byId is not null)
            {
                found.Add(byId.Summary());
                continue;
            }

            var exact = all.Where(d => string.Equals(d.Name, reference, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = exact.Count > 0 ? exact : all.Where(d => d.Name.Contains(reference, StringComparison.OrdinalIgnoreCase)).ToList();
            switch (matches.Count)
            {
                case 0:
                    notFound.Add(reference);
                    break;
                case 1:
                    found.Add(matches[0].Summary());
                    break;
                default:
                    ambiguous.Add(reference);
                    break;
            }
        }

        return new DeviceResolution(found, notFound, ambiguous);
    }

    public async Task<DeviceDetail?> GetDetailAsync(FleetAccess access, Guid deviceId, CancellationToken cancellationToken)
    {
        var d = (await BuildAsync(access.TenantId, cancellationToken)).FirstOrDefault(x => x.Id == deviceId);
        if (d is null)
        {
            return null;
        }

        return new DeviceDetail(
            d.Summary(), d.CpuModel, d.Cores, d.RamGb * Gb,
            [new DiskInfo("C:", "NTFS", d.TotalBytes, d.FreeBytes, d.Encrypted)],
            "F.42", $"SN{Math.Abs(d.Name.GetHashCode(StringComparison.Ordinal)):D8}", $"10.{d.Index % 200}.{d.Index % 250}.{10 + d.Index}", "1.4.2",
            clock.UtcNow.AddDays(-d.UptimeDays), d.PendingReboot);
    }

    public async Task<PerformanceReport?> GetPerformanceAsync(FleetAccess access, Guid deviceId, TimeSpan window, CancellationToken cancellationToken)
    {
        var d = (await BuildAsync(access.TenantId, cancellationToken)).FirstOrDefault(x => x.Id == deviceId);
        if (d is null || d.Cpu is null)
        {
            return null;
        }

        var points = Enumerable.Range(0, 24)
            .Select(i => new PerformancePoint(clock.UtcNow.AddHours(-(23 - i) * window.TotalHours / 24), Math.Clamp(d.Cpu.Value + (((i * 7) % 11) - 5), 1, 100), Math.Clamp(d.Memory!.Value + (((i * 5) % 7) - 3), 1, 100)))
            .ToList();
        return new PerformanceReport(
            d.Id, d.Name, window, points.Count * 12,
            new MetricStats(points.Average(p => p.CpuPercent), points.Select(p => p.CpuPercent).OrderBy(x => x).ElementAt(22), points.Max(p => p.CpuPercent)),
            new MetricStats(points.Average(p => p.MemoryPercent), points.Select(p => p.MemoryPercent).OrderBy(x => x).ElementAt(22), points.Max(p => p.MemoryPercent)),
            new MetricStats(Math.Min(100, d.Cpu.Value / 2), Math.Min(100, d.Cpu.Value / 2 + 10), Math.Min(100, d.Cpu.Value / 2 + 15)),
            d.TopProcesses, points);
    }

    public async Task<HardwareDiagnostics?> GetHardwareDiagnosticsAsync(FleetAccess access, Guid deviceId, CancellationToken cancellationToken)
    {
        var d = (await BuildAsync(access.TenantId, cancellationToken)).FirstOrDefault(x => x.Id == deviceId);
        return d is null
            ? null
            : new HardwareDiagnostics(
                d.Id, d.Name,
                [new DiskHealth("Disk 0", d.DiskFailing ? "Warning" : "Healthy", d.DiskFailing ? 91 : 12, d.DiskFailing ? 120 : 0, 38)],
                d.IsLaptop ? new BatteryHealth(d.Index % 4 == 0 ? 48 : 88, 300 + d.Index) : null,
                false, 55, d.UptimeDays, d.DiskFailing ? ["Disk controller reset (event 129)"] : []);
    }

    public async Task<IReadOnlyList<InstalledSoftware>?> GetInstalledSoftwareAsync(FleetAccess access, Guid deviceId, string? search, CancellationToken cancellationToken)
    {
        var d = (await BuildAsync(access.TenantId, cancellationToken)).FirstOrDefault(x => x.Id == deviceId);
        return d?.Software.Where(s => string.IsNullOrEmpty(search) || s.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<IReadOnlyList<FleetSoftwareEntry>> FindSoftwareAsync(FleetAccess access, SoftwareSearchCriteria criteria, int take, CancellationToken cancellationToken)
    {
        var devices = Filter(await BuildAsync(access.TenantId, cancellationToken), new DeviceSearchCriteria { BranchId = criteria.Scope.BranchId, DepartmentIds = criteria.Scope.DepartmentIds });
        return devices
            .SelectMany(d => d.Software.Select(s => (Device: d, Software: s)))
            .Where(x => (criteria.Name is null || x.Software.Name.Contains(criteria.Name, StringComparison.OrdinalIgnoreCase))
                && (criteria.Publisher is null || (x.Software.Publisher ?? string.Empty).Contains(criteria.Publisher, StringComparison.OrdinalIgnoreCase))
                && (!criteria.OutdatedOnly || x.Software.IsOutdated))
            .GroupBy(x => (x.Software.Name, x.Software.Version))
            .Select(g => new FleetSoftwareEntry(g.Key.Name, g.First().Software.Publisher, g.Key.Version, g.First().Software.LatestVersion, g.First().Software.IsOutdated, g.Count(), g.Select(x => x.Device.Name).Take(5).ToList()))
            .OrderByDescending(e => e.DeviceCount)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
    }

    public async Task<SecurityPosture> GetSecurityPostureAsync(FleetAccess access, FleetScope scope, CancellationToken cancellationToken)
    {
        var devices = Filter(await BuildAsync(access.TenantId, cancellationToken), new DeviceSearchCriteria { BranchId = scope.BranchId, DepartmentIds = scope.DepartmentIds }).ToList();
        var findings = new List<SecurityFinding>();
        foreach (var d in devices)
        {
            if (d.FirewallOff)
            {
                findings.Add(new(SecurityFindingKind.FirewallDisabled, FindingSeverity.High, d.Id, d.Name, "Windows Firewall is disabled."));
            }

            if (!d.Encrypted && d.IsLaptop)
            {
                findings.Add(new(SecurityFindingKind.DiskNotEncrypted, FindingSeverity.High, d.Id, d.Name, "System drive is not encrypted."));
            }

            if (d.PatchesOverdue)
            {
                findings.Add(new(SecurityFindingKind.PatchesOverdue, FindingSeverity.Medium, d.Id, d.Name, "Last security patch installed more than 45 days ago."));
            }

            if (d.PendingReboot)
            {
                findings.Add(new(SecurityFindingKind.PendingReboot, FindingSeverity.Low, d.Id, d.Name, "A restart is pending to finish updates."));
            }

            if (d.Status == DeviceConnectivity.Offline && d.LastSeen < clock.UtcNow.AddDays(-7))
            {
                findings.Add(new(SecurityFindingKind.StaleAgent, FindingSeverity.Medium, d.Id, d.Name, "Agent has not reported for over 7 days."));
            }

            if (d.Os.Contains("Windows 10", StringComparison.Ordinal))
            {
                findings.Add(new(SecurityFindingKind.OsUnsupported, FindingSeverity.Medium, d.Id, d.Name, "Windows 10 reaches end of support."));
            }
        }

        return new SecurityPosture(devices.Count, findings);
    }

    // ------------------------------------------------------------------ generation

    private IEnumerable<DemoDevice> Filter(IReadOnlyList<DemoDevice> all, DeviceSearchCriteria c)
    {
        var now = clock.UtcNow;
        return all.Where(d =>
            (c.Text is null || d.Name.Contains(c.Text, StringComparison.OrdinalIgnoreCase))
            && (c.Status is null || d.Status == c.Status)
            && (c.BranchId is null || d.BranchId == c.BranchId)
            && (c.DepartmentIds is null || c.DepartmentIds.Count == 0 || (d.DepartmentId is { } dep && c.DepartmentIds.Contains(dep)))
            && (c.MaxFreeStorageBytes is null || d.FreeBytes < c.MaxFreeStorageBytes)
            && (c.MinCpuUsagePercent is null || d.Cpu >= c.MinCpuUsagePercent)
            && (c.MinMemoryUsagePercent is null || d.Memory >= c.MinMemoryUsagePercent)
            && (c.OperatingSystem is null || d.Os.Contains(c.OperatingSystem, StringComparison.OrdinalIgnoreCase))
            && (c.OfflineFor is null || (d.Status != DeviceConnectivity.Online && d.LastSeen <= now - c.OfflineFor))
            && (c.InstalledSoftware is null || d.Software.Any(s => s.Name.Contains(c.InstalledSoftware, StringComparison.OrdinalIgnoreCase)))
            && (c.HasOutdatedSoftware is null || d.Software.Any(s => s.IsOutdated) == c.HasOutdatedSoftware));
    }

    private async Task<IReadOnlyList<DemoDevice>> BuildAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var branchPage = await branches.ListAsync(new BranchListQuery(null, false, 1, 200), cancellationToken);
        var departmentPage = await departments.ListAsync(new DepartmentListQuery(null, null, false, 1, 200), cancellationToken);
        var branchList = branchPage.Items.OrderBy(b => b.Code, StringComparer.Ordinal).ToList();
        var now = clock.UtcNow;
        var devices = new List<DemoDevice>();

        string[] prefixes = ["POS", "FIN-PC", "LAP", "DSK"];
        for (var i = 1; i <= 48; i++)
        {
            var kind = i <= 24 ? 0 : i <= 28 ? 1 : i <= 38 ? 2 : 3;
            var number = kind switch { 0 => i, 1 => i - 24, 2 => i - 28, _ => i - 38 };
            var name = $"{prefixes[kind]}-{number:D3}";
            var branch = branchList.Count == 0 ? null : branchList[(i - 1) % branchList.Count];
            var branchDepartments = departmentPage.Items.Where(d => d.BranchId == branch?.Id).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
            var department = kind == 1
                ? branchDepartments.FirstOrDefault(d => d.Name.Contains("Finance", StringComparison.OrdinalIgnoreCase)) ?? departmentPage.Items.FirstOrDefault(d => d.Name.Contains("Finance", StringComparison.OrdinalIgnoreCase))
                : branchDepartments.Count == 0 ? null : branchDepartments[i % branchDepartments.Count];
            if (kind == 1 && department is not null && branch is not null && department.BranchId != branch.Id)
            {
                branch = branchList.First(b => b.Id == department.BranchId);
            }

            var isOffline = i % 7 == 4;
            var isUnknown = i % 23 == 0;
            var status = isUnknown ? DeviceConnectivity.Unknown : isOffline ? DeviceConnectivity.Offline : DeviceConnectivity.Online;
            var cpu = status == DeviceConnectivity.Online ? 8.0 + (i * 13 % 40) : (double?)null;
            var memory = status == DeviceConnectivity.Online ? 30.0 + (i * 17 % 50) : (double?)null;
            IReadOnlyList<ProcessUsage> top = [new("svchost.exe", (cpu ?? 0) * 0.3, 180L * 1024 * 1024), new("chrome.exe", (cpu ?? 0) * 0.2, 900L * 1024 * 1024)];
            if (name == "POS-018")
            {
                status = DeviceConnectivity.Online;
                cpu = 97;
                memory = 88;
                top = [new("SearchIndexer.exe", 62, 420L * 1024 * 1024), new("MsMpEng.exe", 21, 610L * 1024 * 1024), new("posapp.exe", 9, 350L * 1024 * 1024)];
            }

            var total = (kind == 2 ? 512 : 256) * Gb;
            var free = (i % 6 == 3 ? 4 + (i % 5) : 40 + (i * 11 % 150)) * Gb;
            var laptop = kind == 2;
            devices.Add(new DemoDevice
            {
                Index = i,
                Id = StableGuid(tenantId, name),
                Name = name,
                Status = status,
                LastSeen = status == DeviceConnectivity.Online ? now.AddSeconds(-(i % 90)) : now.AddHours(-(6 + (i * 29 % 240))),
                BranchId = branch?.Id,
                BranchName = branch?.Name,
                DepartmentId = department?.Id,
                DepartmentName = department?.Name,
                Os = i % 5 == 0 ? "Windows 10 Pro" : "Windows 11 Pro",
                OsVersion = i % 5 == 0 ? "22H2" : "23H2",
                Manufacturer = laptop ? "Lenovo" : "Dell",
                Model = laptop ? "ThinkPad T14" : "OptiPlex 3000",
                Cpu = cpu,
                Memory = memory,
                FreeBytes = free,
                TotalBytes = total,
                CpuModel = "Intel Core i5-1235U",
                Cores = 10,
                RamGb = laptop ? 16 : 8,
                IsLaptop = laptop,
                Encrypted = !laptop || i % 5 != 0,
                FirewallOff = i % 11 == 0,
                PatchesOverdue = i % 9 == 0,
                PendingReboot = i % 8 == 2,
                DiskFailing = i % 13 == 5,
                UptimeDays = 1 + (i * 3 % 45),
                TopProcesses = top,
                Software = SoftwareFor(i),
            });
        }

        return devices;
    }

    private static List<InstalledSoftware> SoftwareFor(int i) =>
    [
        new("Google Chrome", "Google LLC", i % 3 == 0 ? "119.0.6045" : "126.0.6478", "126.0.6478", i % 3 == 0, null),
        new("7-Zip", "Igor Pavlov", i % 4 == 0 ? "22.01" : "24.08", "24.08", i % 4 == 0, null),
        new("Adobe Acrobat Reader", "Adobe", i % 6 == 0 ? "23.001" : "24.002", "24.002", i % 6 == 0, null),
        new("Microsoft Office", "Microsoft", "16.0.17328", "16.0.17328", false, null),
        new("Solvix Agent", "Solvix", "1.4.2", "1.4.2", false, null),
    ];

    private static Guid StableGuid(Guid tenantId, string name) => new(MD5.HashData(Encoding.UTF8.GetBytes(tenantId.ToString("N") + ":" + name)));

    private sealed class DemoDevice
    {
        public int Index { get; init; }

        public Guid Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public DeviceConnectivity Status { get; init; }

        public DateTimeOffset LastSeen { get; init; }

        public Guid? BranchId { get; init; }

        public string? BranchName { get; init; }

        public Guid? DepartmentId { get; init; }

        public string? DepartmentName { get; init; }

        public string Os { get; init; } = string.Empty;

        public string OsVersion { get; init; } = string.Empty;

        public string Manufacturer { get; init; } = string.Empty;

        public string Model { get; init; } = string.Empty;

        public double? Cpu { get; init; }

        public double? Memory { get; init; }

        public long FreeBytes { get; init; }

        public long TotalBytes { get; init; }

        public string CpuModel { get; init; } = string.Empty;

        public int Cores { get; init; }

        public int RamGb { get; init; }

        public bool IsLaptop { get; init; }

        public bool Encrypted { get; init; }

        public bool FirewallOff { get; init; }

        public bool PatchesOverdue { get; init; }

        public bool PendingReboot { get; init; }

        public bool DiskFailing { get; init; }

        public double UptimeDays { get; init; }

        public IReadOnlyList<ProcessUsage> TopProcesses { get; init; } = [];

        public IReadOnlyList<InstalledSoftware> Software { get; init; } = [];

        public DeviceSummary Summary() => new(Id, Name, Status, LastSeen, BranchId, BranchName, DepartmentId, DepartmentName, Os, OsVersion, Manufacturer, Model, Cpu, Memory, FreeBytes, TotalBytes);
    }
}
