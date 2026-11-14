namespace Solvix.Agent.Windows.Inventory;

using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Inventory;

/// <summary>Operating system and Windows version information.</summary>
[SupportedOSPlatform("windows")]
public sealed class OperatingSystemCollector : IInventoryCollector
{
    public string Name => "operating-system";

    public Task CollectAsync(InventoryBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var os = Wmi.Query(Wmi.Cimv2, "Win32_OperatingSystem", "Caption", "Version", "BuildNumber", "OSArchitecture", "ProductType", "InstallDate", "LastBootUpTime", "Locale").FirstOrDefault();
        using var current = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

        var build = int.TryParse(os?.Text("BuildNumber") ?? current?.GetValue("CurrentBuildNumber") as string, NumberStyles.None, CultureInfo.InvariantCulture, out var b)
            ? b
            : Environment.OSVersion.Version.Build;
        var productType = (WindowsVersionInfo.ProductType)(os?.Int("ProductType") ?? 1);
        var productName = current?.GetValue("ProductName") as string;
        var editionId = current?.GetValue("EditionID") as string;
        var displayVersion = current?.GetValue("DisplayVersion") as string
            ?? current?.GetValue("ReleaseId") as string
            ?? WindowsVersionInfo.DisplayVersionFor(build, productType);

        builder.OperatingSystem = new OperatingSystemInfo
        {
            Name = WindowsVersionInfo.FriendlyName(build, productType, productName, editionId),
            Family = WindowsVersionInfo.Family(build, productType),
            Edition = editionId,
            Version = os?.Text("Version") ?? Environment.OSVersion.Version.ToString(3),
            DisplayVersion = displayVersion,
            BuildNumber = build.ToString(CultureInfo.InvariantCulture),
            UpdateBuildRevision = current?.GetValue("UBR") is int ubr ? ubr : null,
            Architecture = os?.Text("OSArchitecture") switch
            {
                { } a when a.Contains("ARM", StringComparison.OrdinalIgnoreCase) => "arm64",
                { } a when a.Contains("64", StringComparison.Ordinal) => "x64",
                { } => "x86",
                null => null,
            },
            ProductType = productType.ToString(),
            IsSupported = WindowsVersionInfo.IsSupported(build, productType),
            InstallDateUtc = os?.Date("InstallDate"),
            LastBootUpTimeUtc = os?.Date("LastBootUpTime"),
            Locale = CultureInfo.InstalledUICulture.Name,
            TimeZone = TimeZoneInfo.Local.Id,
        };
        return Task.CompletedTask;
    }
}
