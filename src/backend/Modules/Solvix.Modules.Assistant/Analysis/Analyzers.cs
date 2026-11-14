namespace Solvix.Modules.Assistant.Analysis;

using System.Globalization;
using Solvix.SharedKernel.Fleet;

/// <summary>A deterministic observation computed by the backend. The model explains findings; it does not invent them.</summary>
public sealed record Finding(FindingSeverity Severity, string Code, string Title, string Evidence, string Recommendation);

public static class PerformanceAnalyzer
{
    public static IReadOnlyList<Finding> Analyze(PerformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var findings = new List<Finding>();
        var window = $"{report.Window.TotalHours:0.#} h";

        if (report.SampleCount < 5)
        {
            findings.Add(new(FindingSeverity.Info, "limited_data", "Limited telemetry", $"Only {report.SampleCount} samples in the last {window}.", "Treat conclusions as provisional; check that the agent is reporting regularly."));
        }

        if (report.Cpu.Average >= 85 || report.Cpu.P95 >= 95)
        {
            var top = report.TopProcesses.OrderByDescending(p => p.CpuPercent).FirstOrDefault();
            var evidence = $"CPU avg {Pct(report.Cpu.Average)}, p95 {Pct(report.Cpu.P95)}, max {Pct(report.Cpu.Max)} over {window}.";
            if (top is not null)
            {
                evidence += $" Top process: {top.Name} at {Pct(top.CpuPercent)} CPU.";
            }

            findings.Add(new(report.Cpu.Average >= 90 ? FindingSeverity.High : FindingSeverity.Medium, "cpu_sustained_high", "Sustained high CPU usage", evidence,
                top is null ? "Collect diagnostics to identify the busy process." : $"Investigate '{top.Name}' (runaway process, scan, update or malware); collect diagnostics before restarting anything."));
        }

        if (report.Memory.Average >= 90)
        {
            findings.Add(new(FindingSeverity.High, "memory_pressure", "Memory pressure", $"Memory avg {Pct(report.Memory.Average)}, max {Pct(report.Memory.Max)}.", "Look for a leaking process or add RAM; a restart is a temporary fix."));
        }
        else if (report.Memory.Average >= 80)
        {
            findings.Add(new(FindingSeverity.Medium, "memory_high", "High memory usage", $"Memory avg {Pct(report.Memory.Average)}.", "Review startup items and the largest processes."));
        }

        if (report.DiskBusy is { Average: >= 90 })
        {
            findings.Add(new(FindingSeverity.Medium, "disk_busy", "Disk saturated", $"Disk busy avg {Pct(report.DiskBusy.Average)}.", "Check for indexing, backup or antivirus scans; verify disk health."));
        }

        return findings;
    }

    private static string Pct(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + "%";
}

public static class HardwareAnalyzer
{
    public static IReadOnlyList<Finding> Analyze(HardwareDiagnostics hw, DeviceDetail? detail)
    {
        ArgumentNullException.ThrowIfNull(hw);
        var findings = new List<Finding>();

        foreach (var disk in hw.Disks)
        {
            if (!disk.SmartStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase) && !disk.SmartStatus.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new(FindingSeverity.Critical, "disk_smart_failing", $"Disk {disk.Name} reports SMART status '{disk.SmartStatus}'", "SMART self-assessment is not healthy.", "Back up immediately and replace the drive."));
            }

            if (disk.WearPercent is >= 80)
            {
                findings.Add(new(FindingSeverity.High, "disk_wear", $"Disk {disk.Name} is {disk.WearPercent}% worn", "SSD endurance is nearly exhausted.", "Plan a replacement."));
            }

            if (disk.ReallocatedSectors is > 0)
            {
                findings.Add(new(disk.ReallocatedSectors >= 50 ? FindingSeverity.High : FindingSeverity.Medium, "disk_reallocated", $"Disk {disk.Name} has {disk.ReallocatedSectors} reallocated sectors", "Reallocated sectors indicate media degradation.", "Monitor closely and schedule replacement if the count grows."));
            }

            if (disk.TemperatureCelsius is >= 70)
            {
                findings.Add(new(FindingSeverity.Medium, "disk_hot", $"Disk {disk.Name} runs hot ({disk.TemperatureCelsius}°C)", "Sustained high temperature shortens drive life.", "Check airflow and dust."));
            }
        }

        if (hw.Battery is { HealthPercent: < 60 } battery)
        {
            findings.Add(new(battery.HealthPercent < 40 ? FindingSeverity.High : FindingSeverity.Medium, "battery_wear", $"Battery health {battery.HealthPercent}%", $"{battery.CycleCount} charge cycles.", "Schedule a battery replacement."));
        }

        if (hw.MemoryErrorsDetected)
        {
            findings.Add(new(FindingSeverity.High, "memory_errors", "Memory errors detected", "The OS or firmware logged RAM errors.", "Run a full memory test and replace faulty modules."));
        }

        if (hw.CpuTemperatureCelsius is >= 90)
        {
            findings.Add(new(FindingSeverity.High, "cpu_hot", $"CPU temperature {hw.CpuTemperatureCelsius}°C", "Thermal throttling is likely.", "Clean the cooler, re-paste, check fans."));
        }

        if (hw.UptimeDays >= 30)
        {
            findings.Add(new(FindingSeverity.Low, "long_uptime", $"Up for {hw.UptimeDays:0} days without a restart", "Pending updates and leaked resources accumulate.", "Schedule a restart in a maintenance window."));
        }

        if (detail is not null)
        {
            foreach (var disk in detail.Disks.Where(d => d.TotalBytes > 0))
            {
                var freePct = disk.FreeBytes * 100.0 / disk.TotalBytes;
                if (freePct < 10)
                {
                    findings.Add(new(freePct < 5 ? FindingSeverity.High : FindingSeverity.Medium, "low_disk_space", $"Volume {disk.Name} has {freePct:0.#}% free", $"{disk.FreeBytes / 1_073_741_824.0:0.#} GB free of {disk.TotalBytes / 1_073_741_824.0:0.#} GB.", "Clear temporary files and review large folders."));
                }
            }
        }

        return findings;
    }
}

public sealed record SecurityAdvice(
    int TotalDevices,
    IReadOnlyDictionary<string, int> CountsByKind,
    IReadOnlyList<SecurityFinding> TopFindings,
    IReadOnlyList<string> Recommendations);

public static class SecurityAdvisor
{
    private static readonly Dictionary<SecurityFindingKind, string> Recommendation = new()
    {
        [SecurityFindingKind.NoAntivirus] = "Install and enable endpoint protection on devices without it.",
        [SecurityFindingKind.AntivirusOutdated] = "Update antivirus definitions; investigate devices that cannot reach the update source.",
        [SecurityFindingKind.FirewallDisabled] = "Re-enable the host firewall and enforce it through policy.",
        [SecurityFindingKind.DiskNotEncrypted] = "Enable full-disk encryption, starting with laptops.",
        [SecurityFindingKind.OsUnsupported] = "Upgrade or retire devices running an unsupported operating system.",
        [SecurityFindingKind.PatchesOverdue] = "Deploy overdue OS patches in a maintenance window.",
        [SecurityFindingKind.PendingReboot] = "Reboot devices with pending updates so patches take effect.",
        [SecurityFindingKind.StaleAgent] = "Investigate devices that have not checked in; they are unmanaged and unpatched.",
    };

    public static SecurityAdvice Advise(SecurityPosture posture, int topN = 15)
    {
        ArgumentNullException.ThrowIfNull(posture);
        var counts = posture.Findings.GroupBy(f => f.Kind).OrderByDescending(g => g.Max(f => f.Severity)).ThenByDescending(g => g.Count()).ToList();
        return new SecurityAdvice(
            posture.TotalDevices,
            counts.ToDictionary(g => g.Key.ToString(), g => g.Count()),
            posture.Findings.OrderByDescending(f => f.Severity).ThenBy(f => f.DeviceName, StringComparer.OrdinalIgnoreCase).Take(topN).ToList(),
            counts.Select(g => $"{Recommendation[g.Key]} ({g.Count()} device(s), highest severity {g.Max(f => f.Severity)})").ToList());
    }
}
