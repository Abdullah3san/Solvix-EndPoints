namespace Solvix.Agent.Windows.Inventory;

using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Inventory;

/// <summary>
/// Installed applications from the Uninstall registry keys: 64-bit and 32-bit
/// machine-wide installs and per-user installs of loaded user profiles.
/// System components and update entries are skipped.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SoftwareCollector : IInventoryCollector
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public string Name => "software";

    public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        using (var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            Read(hklm64, "x64", builder, cancellationToken);
        }

        if (Environment.Is64BitOperatingSystem)
        {
            using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            Read(hklm32, "x86", builder, cancellationToken);
        }

        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        foreach (var sid in users.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !s.EndsWith("_Classes", StringComparison.Ordinal)))
        {
            using var profile = users.OpenSubKey(sid);
            if (profile is not null)
            {
                Read(profile, "User", builder, cancellationToken);
            }
        }

        return Task.CompletedTask;
    }

    private static void Read(RegistryKey root, string scope, InventoryBuilder builder, CancellationToken cancellationToken)
    {
        using var uninstall = root.OpenSubKey(UninstallPath);
        if (uninstall is null)
        {
            return;
        }

        foreach (var name in uninstall.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var app = uninstall.OpenSubKey(name);
            if (app?.GetValue("DisplayName") is not string displayName || string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            if (app.GetValue("SystemComponent") is int system && system == 1)
            {
                continue;
            }

            if (app.GetValue("ParentKeyName") is string || app.GetValue("ReleaseType") is "Update" or "Hotfix" or "Security Update")
            {
                continue;
            }

            builder.Software.Add(new InstalledApplication
            {
                Name = displayName.Trim(),
                Version = (app.GetValue("DisplayVersion") as string)?.Trim(),
                Publisher = (app.GetValue("Publisher") as string)?.Trim(),
                InstallDate = DateOnly.TryParseExact(app.GetValue("InstallDate") as string, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                Scope = scope,
                InstallLocation = (app.GetValue("InstallLocation") as string)?.Trim() is { Length: > 0 } location ? location : null,
                EstimatedSizeBytes = app.GetValue("EstimatedSize") is int kb ? kb * 1024L : null,
            });
        }
    }
}
