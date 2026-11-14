namespace Solvix.Modules.Assistant.Tools;

using System.Text.Json.Nodes;
using Solvix.Modules.Assistant.Analysis;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Fleet;
using Solvix.SharedKernel.Identity;

internal static class FleetView
{
    public const double Gb = 1024.0 * 1024 * 1024;

    public static object Device(DeviceSummary d) => new
    {
        d.Id,
        d.Name,
        status = d.Status.ToString(),
        lastSeenUtc = d.LastSeenUtc,
        branch = d.BranchName,
        department = d.DepartmentName,
        os = d.OperatingSystem,
        d.OsVersion,
        d.Manufacturer,
        d.Model,
        cpuPercent = d.CpuUsagePercent is { } c ? Math.Round(c, 1) : (double?)null,
        memoryPercent = d.MemoryUsagePercent is { } m ? Math.Round(m, 1) : (double?)null,
        freeStorageGb = d.FreeStorageBytes is { } f ? Math.Round(f / Gb, 1) : (double?)null,
        totalStorageGb = d.TotalStorageBytes is { } t ? Math.Round(t / Gb, 1) : (double?)null,
    };

    public static object FindingView(Finding f) => new { severity = f.Severity.ToString(), f.Code, f.Title, f.Evidence, f.Recommendation };

    /// <summary>Resolves exactly one device from a name or id inside the caller's access.</summary>
    public static async Task<DeviceSummary> ResolveOneAsync(IFleetQueryService fleet, ToolContext ctx, string reference, CancellationToken ct)
    {
        var resolution = await fleet.ResolveAsync(ctx.Principal.ToFleetAccess(), [reference], ct);
        if (resolution.Ambiguous.Count > 0)
        {
            throw new ToolInputException($"'{reference}' matches several devices; use the exact name or id.");
        }

        return resolution.Devices.Count == 1 ? resolution.Devices[0] : throw new ToolInputException($"No accessible device matches '{reference}'.");
    }

    public static void EnsureFleet(IFleetQueryService fleet)
    {
        if (!fleet.IsAvailable)
        {
            throw new ServiceUnavailableException("No fleet data source is connected to this workspace yet.", "fleet_unavailable");
        }
    }
}

internal sealed class ListBranchesTool(IBranchService branches) : IAssistantTool
{
    public string Name => "list_branches";

    public string Description => "List the company's branches (name, code, headquarters flag). Use to resolve branch names before filtering the fleet.";

    public TenantRole MinimumRole => TenantRole.Member;

    public JsonNode InputSchema => Schema.Object(new JsonObject { ["search"] = Schema.Str("Optional name/code filter.") });

    public async Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        var page = await branches.ListAsync(new BranchListQuery(args.OptionalString("search", 100), false, 1, 100), cancellationToken);
        return new { total = page.TotalCount, branches = page.Items.Select(b => new { b.Id, b.Name, b.Code, b.IsHeadquarters, b.TimeZone }) };
    }
}

internal sealed class ListDepartmentsTool(IDepartmentService departments, ScopeResolver scopes) : IAssistantTool
{
    public string Name => "list_departments";

    public string Description => "List departments, optionally within one branch.";

    public TenantRole MinimumRole => TenantRole.Member;

    public JsonNode InputSchema => Schema.Object(new JsonObject
    {
        ["branch"] = Schema.Str("Branch name or code."),
        ["search"] = Schema.Str("Optional name filter."),
    });

    public async Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        Guid? branchId = null;
        if (args.OptionalString("branch", 100) is { } branch)
        {
            branchId = (await scopes.ResolveBranchAsync(branch, cancellationToken)).Id;
        }

        var page = await departments.ListAsync(new DepartmentListQuery(branchId, args.OptionalString("search", 100), false, 1, 100), cancellationToken);
        return new { total = page.TotalCount, departments = page.Items.Select(d => new { d.Id, d.Name, d.Code, branch = d.BranchName }) };
    }
}

internal sealed class GetFleetSummaryTool(IFleetQueryService fleet, ScopeResolver scopes) : IAssistantTool
{
    public string Name => "get_fleet_summary";

    public string Description => "Online/offline/unknown counts, low-storage count, OS distribution and per-branch breakdown. Use for fleet-wide questions and online/offline analysis.";

    public TenantRole MinimumRole => TenantRole.Member;

    public JsonNode InputSchema => Schema.Object(new JsonObject
    {
        ["branch"] = Schema.Str("Limit to a branch (name or code)."),
        ["department"] = Schema.Str("Limit to a department (name)."),
    });

    public async Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        FleetView.EnsureFleet(fleet);
        var scope = await scopes.ResolveAsync(args.OptionalString("branch", 100), args.OptionalString("department", 100), cancellationToken);
        var s = await fleet.GetSummaryAsync(context.Principal.ToFleetAccess(), scope, cancellationToken);
        return new
        {
            s.Total,
            s.Online,
            s.Offline,
            s.Unknown,
            offlinePercent = s.Total == 0 ? 0 : Math.Round(s.Offline * 100.0 / s.Total, 1),
            lowStorageDevices = s.LowStorage,
            operatingSystems = s.ByOperatingSystem,
            branches = s.ByBranch,
        };
    }
}

internal sealed class SearchDevicesTool(IFleetQueryService fleet, ScopeResolver scopes) : IAssistantTool
{
    public string Name => "search_devices";

    public string Description =>
        "Search managed computers. Combine filters: connectivity status, branch, department, low free storage, high CPU/memory, OS, offline duration, installed software, outdated software. " +
        "Returns up to 'limit' devices plus the total match count.";

    public TenantRole MinimumRole => TenantRole.Member;

    public JsonNode InputSchema => Schema.Object(new JsonObject
    {
        ["text"] = Schema.Str("Name/hostname substring."),
        ["status"] = Schema.Enum("Connectivity status.", ["online", "offline", "unknown"]),
        ["branch"] = Schema.Str("Branch name or code."),
        ["department"] = Schema.Str("Department name."),
        ["max_free_storage_gb"] = Schema.Num("Only devices with less free storage than this (GB)."),
        ["min_cpu_percent"] = Schema.Num("Only devices with at least this CPU usage."),
        ["min_memory_percent"] = Schema.Num("Only devices with at least this memory usage."),
        ["os"] = Schema.Str("Operating system substring, e.g. 'Windows 10'."),
        ["offline_for_hours"] = Schema.Int("Only devices not seen for at least this many hours.", 1, 8760),
        ["installed_software"] = Schema.Str("Software name substring the device must have installed."),
        ["has_outdated_software"] = Schema.Bool("Only devices with (true) or without (false) outdated software."),
        ["sort"] = Schema.Enum("Sort order.", ["name", "last_seen", "cpu_usage_desc", "memory_usage_desc", "free_storage_asc"]),
        ["limit"] = Schema.Int("Max devices to return (default 25).", 1, 50),
    });

    public async Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        FleetView.EnsureFleet(fleet);
        var scope = await scopes.ResolveAsync(args.OptionalString("branch", 100), args.OptionalString("department", 100), cancellationToken);
        var limit = args.Int("limit", 1, 50, 25);
        var criteria = new DeviceSearchCriteria
        {
            Text = args.OptionalString("text", 100),
            Status = args.OptionalEnum<DeviceConnectivity>("status"),
            BranchId = scope.BranchId,
            DepartmentIds = scope.DepartmentIds,
            MaxFreeStorageBytes = args.OptionalNumber("max_free_storage_gb", 0, 100000) is { } gb ? (long)(gb * FleetView.Gb) : null,
            MinCpuUsagePercent = args.OptionalNumber("min_cpu_percent", 0, 100),
            MinMemoryUsagePercent = args.OptionalNumber("min_memory_percent", 0, 100),
            OperatingSystem = args.OptionalString("os", 100),
            OfflineFor = args.Has("offline_for_hours") ? TimeSpan.FromHours(args.Int("offline_for_hours", 1, 8760, 1)) : null,
            InstalledSoftware = args.OptionalString("installed_software", 100),
            HasOutdatedSoftware = args.OptionalBool("has_outdated_software"),
            Sort = ParseSort(args.OptionalString("sort", 30)),
        };

        var (items, total) = await fleet.SearchAsync(context.Principal.ToFleetAccess(), criteria, 0, limit, cancellationToken);
        return new { total, returned = items.Count, truncated = total > items.Count, devices = items.Select(FleetView.Device) };
    }

    private static DeviceSort ParseSort(string? sort) => sort switch
    {
        null or "name" => DeviceSort.Name,
        "last_seen" => DeviceSort.LastSeen,
        "cpu_usage_desc" => DeviceSort.CpuUsageDesc,
        "memory_usage_desc" => DeviceSort.MemoryUsageDesc,
        "free_storage_asc" => DeviceSort.FreeStorageAsc,
        _ => throw new ToolInputException("'sort' must be one of: name, last_seen, cpu_usage_desc, memory_usage_desc, free_storage_asc."),
    };
}

internal sealed class GetDeviceDetailsTool(IFleetQueryService fleet) : IAssistantTool
{
    public string Name => "get_device_details";

    public string Description => "Full details for one device: hardware, disks, OS, agent, last boot, pending reboot.";

    public TenantRole MinimumRole => TenantRole.Member;

    public JsonNode InputSchema => Schema.Object(new JsonObject { ["device"] = Schema.Str("Device name (e.g. POS-018) or id.") }, "device");

    public async Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        FleetView.EnsureFleet(fleet);
        var device = await FleetView.ResolveOneAsync(fleet, context, args.RequiredString("device", 100), cancellationToken);
        var d = await fleet.GetDetailAsync(context.Principal.ToFleetAccess(), device.Id, cancellationToken)
            ?? throw new NotFoundException("Device", device.Id);
        return new
        {
            summary = FleetView.Device(d.Summary),
            d.CpuModel,
            d.CpuCores,
            totalMemoryGb = d.TotalMemoryBytes is { } m ? Math.Round(m / FleetView.Gb, 1) : (double?)null,
            disks = d.Disks.Select(x => new { x.Name, x.FileSystem, totalGb = Math.Round(x.TotalBytes / FleetView.Gb, 1), freeGb = Math.Round(x.FreeBytes / FleetView.Gb, 1), x.Encrypted }),
            d.BiosVersion,
            d.SerialNumber,
            d.IpAddress,
            d.AgentVersion,
            d.LastBootUtc,
            d.PendingReboot,
        };
    }
}

internal sealed class GetDevicePerformanceTool(IFleetQueryService fleet) : IAssistantTool
{
    public string Name => "get_device_performance";

    public string Description => "CPU/memory/disk statistics, top processes and a downsampled timeline for one device, plus backend-computed findings. Use to explain high resource usage.";

    public TenantRole MinimumRole => TenantRole.Member;

    public JsonNode InputSchema => Schema.Object(
        new JsonObject
        {
            ["device"] = Schema.Str("Device name or id."),
            ["window_hours"] = Schema.Int("Look-back window in hours (default 24).", 1, 168),
        },
        "device");

    public async Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        FleetView.EnsureFleet(fleet);
        var device = await FleetView.ResolveOneAsync(fleet, context, args.RequiredString("device", 100), cancellationToken);
        var window = TimeSpan.FromHours(args.Int("window_hours", 1, 168, 24));
        var report = await fleet.GetPerformanceAsync(context.Principal.ToFleetAccess(), device.Id, window, cancellationToken)
            ?? throw new NotFoundException("Performance data for device", device.Name);
        return new
        {
            device = FleetView.Device(device),
            windowHours = report.Window.TotalHours,
            report.SampleCount,
            cpu = report.Cpu,
            memory = report.Memory,
            diskBusy = report.DiskBusy,
            topProcesses = report.TopProcesses.Take(8).Select(p => new { p.Name, cpuPercent = Math.Round(p.CpuPercent, 1), memoryMb = p.MemoryBytes / (1024 * 1024) }),
            timeline = report.Timeline.Take(48),
            findings = PerformanceAnalyzer.Analyze(report).Select(FleetView.FindingView),
        };
    }
}

internal sealed class GetHardwareDiagnosticsTool(IFleetQueryService fleet) : IAssistantTool
{
    public string Name => "get_hardware_diagnostics";

    public string Description => "Hardware health for one device: disk SMART, battery, memory errors, temperatures, uptime, free space, with backend-computed findings.";

    public TenantRole MinimumRole => TenantRole.Member;

    public JsonNode InputSchema => Schema.Object(new JsonObject { ["device"] = Schema.Str("Device name or id.") }, "device");

    public async Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        FleetView.EnsureFleet(fleet);
        var access = context.Principal.ToFleetAccess();
        var device = await FleetView.ResolveOneAsync(fleet, context, args.RequiredString("device", 100), cancellationToken);
        var hw = await fleet.GetHardwareDiagnosticsAsync(access, device.Id, cancellationToken) ?? throw new NotFoundException("Diagnostics for device", device.Name);
        var detail = await fleet.GetDetailAsync(access, device.Id, cancellationToken);
        return new
        {
            device = FleetView.Device(device),
            hw.Disks,
            hw.Battery,
            hw.MemoryErrorsDetected,
            hw.CpuTemperatureCelsius,
            uptimeDays = Math.Round(hw.UptimeDays, 1),
            hw.RecentHardwareEvents,
            findings = HardwareAnalyzer.Analyze(hw, detail).Select(FleetView.FindingView),
        };
    }
}

internal sealed class FindSoftwareTool(IFleetQueryService fleet, ScopeResolver scopes) : IAssistantTool
{
    public string Name => "find_software";

    public string Description => "Fleet-wide software inventory analysis: which software/versions are installed on how many devices, and which are outdated.";

    public TenantRole MinimumRole => TenantRole.Member;

    public JsonNode InputSchema => Schema.Object(new JsonObject
    {
        ["name"] = Schema.Str("Software name substring."),
        ["publisher"] = Schema.Str("Publisher substring."),
        ["outdated_only"] = Schema.Bool("Only software with a newer version available."),
        ["branch"] = Schema.Str("Branch name or code."),
        ["department"] = Schema.Str("Department name."),
        ["limit"] = Schema.Int("Max rows (default 25).", 1, 50),
    });

    public async Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        FleetView.EnsureFleet(fleet);
        var scope = await scopes.ResolveAsync(args.OptionalString("branch", 100), args.OptionalString("department", 100), cancellationToken);
        var criteria = new SoftwareSearchCriteria(args.OptionalString("name", 100), args.OptionalString("publisher", 100), args.OptionalBool("outdated_only") ?? false, scope);
        var rows = await fleet.FindSoftwareAsync(context.Principal.ToFleetAccess(), criteria, args.Int("limit", 1, 50, 25), cancellationToken);
        return new { returned = rows.Count, software = rows };
    }
}

internal sealed class ListDeviceSoftwareTool(IFleetQueryService fleet) : IAssistantTool
{
    public string Name => "list_device_software";

    public string Description => "Software installed on one device, with outdated flags.";

    public TenantRole MinimumRole => TenantRole.Member;

    public JsonNode InputSchema => Schema.Object(
        new JsonObject
        {
            ["device"] = Schema.Str("Device name or id."),
            ["search"] = Schema.Str("Optional name filter."),
            ["limit"] = Schema.Int("Max rows (default 50).", 1, 100),
        },
        "device");

    public async Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        FleetView.EnsureFleet(fleet);
        var device = await FleetView.ResolveOneAsync(fleet, context, args.RequiredString("device", 100), cancellationToken);
        var all = await fleet.GetInstalledSoftwareAsync(context.Principal.ToFleetAccess(), device.Id, args.OptionalString("search", 100), cancellationToken)
            ?? throw new NotFoundException("Software inventory for device", device.Name);
        var limit = args.Int("limit", 1, 100, 50);
        return new { device = device.Name, total = all.Count, outdated = all.Count(s => s.IsOutdated), truncated = all.Count > limit, software = all.Take(limit) };
    }
}

internal sealed class GetSecurityPostureTool(IFleetQueryService fleet, ScopeResolver scopes) : IAssistantTool
{
    public string Name => "get_security_posture";

    public string Description => "Security findings across the fleet (antivirus, firewall, encryption, patches, unsupported OS, stale agents) with prioritised recommendations.";

    public TenantRole MinimumRole => TenantRole.Member;

    public JsonNode InputSchema => Schema.Object(new JsonObject
    {
        ["branch"] = Schema.Str("Branch name or code."),
        ["department"] = Schema.Str("Department name."),
    });

    public async Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        FleetView.EnsureFleet(fleet);
        var scope = await scopes.ResolveAsync(args.OptionalString("branch", 100), args.OptionalString("department", 100), cancellationToken);
        var posture = await fleet.GetSecurityPostureAsync(context.Principal.ToFleetAccess(), scope, cancellationToken);
        var advice = SecurityAdvisor.Advise(posture, 20);
        return new
        {
            advice.TotalDevices,
            countsByKind = advice.CountsByKind,
            recommendations = advice.Recommendations,
            topFindings = advice.TopFindings.Select(f => new { f.DeviceName, kind = f.Kind.ToString(), severity = f.Severity.ToString(), f.Detail }),
        };
    }
}
