namespace Solvix.Agent.Core.Monitoring;

using System.Diagnostics;
using System.Globalization;
using Solvix.Agent.Contracts;

/// <summary>Captures the operating-system health signal sent with each heartbeat.</summary>
public interface ISystemHealthProbe
{
    /// <summary>When the operating system started, derived from the monotonic system tick count.</summary>
    DateTimeOffset GetBootTimeUtc();

    SystemHealthSnapshot Capture();
}

/// <summary>
/// Portable implementation (memory from <c>/proc/meminfo</c> or the GC view,
/// system drive via <see cref="DriveInfo"/>). The Windows host replaces it with
/// a version that also reads native memory status and pending-reboot markers.
/// </summary>
public class PortableSystemHealthProbe(TimeProvider time) : ISystemHealthProbe
{
    public DateTimeOffset GetBootTimeUtc()
    {
        // Rounded to whole seconds: consecutive readings must compare equal for the same boot.
        var boot = time.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return new DateTimeOffset(boot.Ticks - (boot.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    public virtual SystemHealthSnapshot Capture()
    {
        var (total, available) = ReadMemory();
        var (drive, driveTotal, driveFree) = ReadSystemDrive();
        return new SystemHealthSnapshot
        {
            TotalMemoryBytes = total,
            AvailableMemoryBytes = available,
            SystemDrive = drive,
            SystemDriveTotalBytes = driveTotal,
            SystemDriveFreeBytes = driveFree,
            RebootPending = ReadRebootPending(),
            ProcessCount = CountProcesses(),
        };
    }

    protected virtual (long? Total, long? Available) ReadMemory()
    {
        const string meminfo = "/proc/meminfo";
        if (File.Exists(meminfo))
        {
            long? total = null, available = null;
            foreach (var line in File.ReadLines(meminfo))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseKb(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseKb(line);
                }
            }

            return (total, available);
        }

        var info = GC.GetGCMemoryInfo();
        return (info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes : null, null);
    }

    protected virtual bool? ReadRebootPending() => null;

    protected virtual (string? Drive, long? Total, long? Free) ReadSystemDrive()
    {
        try
        {
            var root = OperatingSystem.IsWindows()
                ? Path.GetPathRoot(Environment.SystemDirectory)
                : "/";
            if (string.IsNullOrEmpty(root))
            {
                return (null, null, null);
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.Name, drive.TotalSize, drive.AvailableFreeSpace) : (drive.Name, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null, null);
        }
    }

    private static int? CountProcesses()
    {
        try
        {
            var processes = Process.GetProcesses();
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return processes.Length;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static long? ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : null;
    }
}
