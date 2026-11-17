namespace Solvix.Agent.Windows.Platform;

using System.Runtime.Versioning;
using System.Diagnostics;
using Solvix.Agent.Core.Patching;
using Solvix.Agent.Windows.Providers;
using Solvix.Endpoint.Contracts.Agent;

/// <summary>Restart detection and initiation.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RebootManager : IRebootManager
{
    public DateTimeOffset LastBootUtc
    {
        get
        {
            // Rounded to 10 s so the value is stable between calls despite timer resolution.
            var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            return new DateTimeOffset(boot.Ticks - (boot.Ticks % TimeSpan.FromSeconds(10).Ticks), TimeSpan.Zero);
        }
    }

    public RebootStatus GetRebootStatus()
    {
        var reasons = new List<string>();
        using (var root = RegistryHelper.Hklm())
        {
            if (root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is { } cbs)
            {
                cbs.Dispose();
                reasons.Add("Component Based Servicing");
            }

            if (root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is { } wu)
            {
                wu.Dispose();
                reasons.Add("Windows Update");
            }
        }

        if (RegistryHelper.Read(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations") is { ValueExists: true, Value.Length: > 0 })
        {
            reasons.Add("Pending file rename operations");
        }

        try
        {
            dynamic info = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo", throwOnError: true)!)!;
            if ((bool)info.RebootRequired && !reasons.Contains("Windows Update"))
            {
                reasons.Add("Windows Update");
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Not available on every SKU.
        }

        return new RebootStatus(reasons.Count > 0, reasons, LastBootUtc);
    }

    /// <summary>
    /// shutdown.exe shows the countdown and message to every signed-in user and
    /// records a planned "Operating System: Hot fix" reason in the event log.
    /// </summary>
    public void ScheduleRestart(int gracePeriodSeconds, string message)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("/r");
        info.ArgumentList.Add("/t");
        info.ArgumentList.Add(Math.Clamp(gracePeriodSeconds, 0, 315_360_000).ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("/d");
        info.ArgumentList.Add("p:2:17");
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add(message.Length > 500 ? message[..500] : message);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("shutdown.exe could not be started.");
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"shutdown.exe exited with code {process.ExitCode} (a restart may already be scheduled).");
        }
    }
}
