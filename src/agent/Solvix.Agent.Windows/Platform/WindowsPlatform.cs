namespace Solvix.Agent.Windows.Platform;

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Solvix.Agent.Core.Patching;
using Solvix.Agent.Core.Runtime;
using Solvix.Agent.Windows.Providers;
using Solvix.Endpoint.Contracts.Agent;

/// <summary>Restart detection and initiation.</summary>
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

internal sealed class WindowsDeviceInfoProvider : IDeviceInfoProvider
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public string Hostname => Environment.MachineName;

    public string? DnsDomain
    {
        get
        {
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            return string.IsNullOrWhiteSpace(domain) ? null : domain;
        }
    }

    /// <summary>SMBIOS system UUID (stable across re-imaging), falling back to the Windows MachineGuid.</summary>
    public string? HardwareId =>
        RegistryHelper.Read(@"SYSTEM\HardwareConfig", "LastConfig").Value?.Trim('{', '}').ToLowerInvariant()
        ?? RegistryHelper.Read(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid").Value;

    public string OsName
    {
        get
        {
            var name = RegistryHelper.Read(CurrentVersion, "ProductName").Value ?? "Windows";
            return int.TryParse(OsBuild.Split('.')[0], out var build) && build >= 22000 ? name.Replace("Windows 10", "Windows 11", StringComparison.Ordinal) : name;
        }
    }

    public string OsVersion => RegistryHelper.Read(CurrentVersion, "DisplayVersion").Value ?? Environment.OSVersion.Version.ToString();

    public string OsBuild
    {
        get
        {
            var build = RegistryHelper.Read(CurrentVersion, "CurrentBuild").Value ?? Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var ubr = RegistryHelper.Read(CurrentVersion, "UBR").Value;
            return ubr is null ? build : $"{build}.{ubr}";
        }
    }
}

/// <summary>DPAPI in the service account's (LocalSystem) scope: only SYSTEM can decrypt the device credential.</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Solvix.Agent.DeviceCredential.v1");

    public string Protect(string plaintext) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedText) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser));
}

internal static class StateDirectory
{
    /// <summary>Creates the state directory readable and writable only by SYSTEM and Administrators.</summary>
    public static void Secure(string path)
    {
        var directory = Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        directory.SetAccessControl(security);
    }
}
