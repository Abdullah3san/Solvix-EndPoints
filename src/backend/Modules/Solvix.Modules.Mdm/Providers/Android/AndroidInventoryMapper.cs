namespace Solvix.Modules.Mdm.Providers.Android;

using Solvix.Modules.Mdm.Domain;

/// <summary>Maps Android Management API device resources to Solvix inventory and scenario.</summary>
public static class AndroidInventoryMapper
{
    public static DeviceInventory ToInventory(AndroidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var model = string.Join(' ', new[] { device.Brand, device.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new DeviceInventory
        {
            Manufacturer = device.Manufacturer,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            DisplayName = string.IsNullOrWhiteSpace(model) ? null : model,
            SerialNumber = device.SerialNumber,
            OsVersion = device.AndroidVersion,
            SecurityPatchLevel = device.SecurityPatchLevel,
            IsEncrypted = device.IsEncrypted,
            HasPasscode = device.IsDeviceSecure,
            IsSupervised = null,
            Integrity = device.DevicePosture switch
            {
                "SECURE" => IntegrityState.Intact,
                "AT_RISK" or "POTENTIALLY_COMPROMISED" => IntegrityState.Compromised,
                _ => IntegrityState.Unknown,
            },
            ProviderReportsCompliant = device.PolicyCompliant,
            LastSeenAtUtc = device.LastStatusReportTime,
            AppliedPolicyReference = device.AppliedPolicyName,
            // Android work-profile reports only cover the work profile, so every listed app is managed.
            Apps = device.Applications?
                .Select(a => new InstalledApp(a.PackageName, a.DisplayName, a.VersionName, IsManaged: true))
                .ToList(),
        };
    }

    public static DeviceOwnership ToOwnership(string? value) =>
        string.Equals(value, "PERSONALLY_OWNED", StringComparison.Ordinal) ? DeviceOwnership.PersonallyOwned : DeviceOwnership.CompanyOwned;

    /// <summary>
    /// Infers the scenario of a device that was not enrolled through a Solvix token.
    /// Device owner devices are assumed fully managed (a kiosk policy can be assigned afterwards).
    /// </summary>
    public static ManagementScenario InferScenario(AndroidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var mode = string.Equals(device.ManagementMode, "PROFILE_OWNER", StringComparison.Ordinal)
            ? ManagementMode.WorkProfile
            : ManagementMode.FullyManaged;
        var ownership = mode == ManagementMode.WorkProfile ? ToOwnership(device.Ownership) : DeviceOwnership.CompanyOwned;
        return new ManagementScenario(MobilePlatform.Android, mode, ownership);
    }
}
