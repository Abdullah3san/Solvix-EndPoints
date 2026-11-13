namespace Solvix.Agent.Windows.Software;

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Solvix.Agent.Contracts.Software;
using Solvix.Agent.Core.Software;

/// <summary>
/// Installed applications for detection, from the Uninstall registry keys (64-bit, 32-bit and loaded
/// user profiles). For Windows Installer products the key name is the product code. Reading these
/// keys needs no privileges, so detection runs in the unprivileged agent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UninstallRegistrySource : IInstalledApplicationSource
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<DetectedApplication> GetInstalledApplications()
    {
        var result = new List<DetectedApplication>();
        using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            Read(hklm64, result);
        }

        if (Environment.Is64BitOperatingSystem)
        {
            using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            Read(hklm32, result);
        }

        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        foreach (var sid in users.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !s.EndsWith("_Classes", StringComparison.Ordinal)))
        {
            using var profile = users.OpenSubKey(sid);
            if (profile is not null)
            {
                Read(profile, result);
            }
        }

        return result;
    }

    private static void Read(RegistryKey root, List<DetectedApplication> result)
    {
        using var uninstall = root.OpenSubKey(UninstallPath);
        if (uninstall is null)
        {
            return;
        }

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var app = uninstall.OpenSubKey(name);
            if (app?.GetValue("DisplayName") is not string displayName || string.IsNullOrWhiteSpace(displayName))
            {
                // MSI products registered without a display name can still be found by product code.
                if (DetectionRule.NormalizeProductCode(name) is { } bare && app?.GetValue("WindowsInstaller") is int msi && msi == 1)
                {
                    result.Add(new DetectedApplication(bare, app.GetValue("DisplayVersion") as string, app.GetValue("Publisher") as string, bare));
                }

                continue;
            }

            result.Add(new DetectedApplication(
                displayName.Trim(),
                (app.GetValue("DisplayVersion") as string)?.Trim(),
                (app.GetValue("Publisher") as string)?.Trim(),
                DetectionRule.NormalizeProductCode(name)));
        }
    }
}

/// <summary>
/// The executor's pipe: only LocalSystem and the agent's virtual service account
/// (<c>NT SERVICE\SolvixAgent</c>) may connect, and network access to the pipe is denied, so neither
/// signed-in users nor remote machines can talk to the privileged executor.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecureExecutorPipeFactory : IExecutorPipeFactory
{
    public const string AgentServiceAccount = @"NT SERVICE\SolvixAgent";

    public NamedPipeServerStream Create(string name)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new NTAccount(AgentServiceAccount), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }
}
