namespace Solvix.Modules.Mdm.Domain;

using Solvix.SharedKernel.Exceptions;

/// <summary>What the platform lets an organisation do on one kind of device.</summary>
public sealed record MdmCapabilitySet(
    ManagementScenario Scenario,
    IReadOnlySet<DeviceCommandType> Commands,
    bool CanRemoveCorporateDataOnly,
    bool CanFactoryReset,
    bool CanReadDeviceIdentifiers,
    bool CanEnforceDeviceWidePolicy,
    IReadOnlyList<string> Limitations)
{
    public bool Supports(DeviceCommandType command) => Commands.Contains(command);
}

/// <summary>
/// Single source of truth for platform capabilities and ownership boundaries.
/// The matrix is deliberately conservative: an operation is only offered when
/// its effect on the target is unambiguous. Command handling, policy
/// translation and the published documentation all read from here.
/// </summary>
public static class MdmCapabilities
{
    private static readonly IReadOnlyDictionary<ManagementScenario, MdmCapabilitySet> Matrix = Build();

    public static IReadOnlyCollection<MdmCapabilitySet> All => Matrix.Values.ToList();

    public static MdmCapabilitySet For(ManagementScenario scenario)
    {
        scenario.EnsureValid();
        return Matrix[scenario];
    }

    /// <summary>Commands that destroy data or remove management and therefore need a second person's approval.</summary>
    public static bool RequiresApproval(DeviceCommandType command) =>
        command is DeviceCommandType.Retire or DeviceCommandType.Wipe;

    /// <summary>Throws <see cref="UnsupportedOperationException"/> with the platform-specific reason.</summary>
    public static void EnsureSupported(ManagementScenario scenario, DeviceCommandType command)
    {
        var set = For(scenario);
        if (set.Supports(command))
        {
            return;
        }

        var reason = command switch
        {
            DeviceCommandType.Wipe when scenario.IsPersonalBoundary =>
                "A full wipe is never available on personally owned devices. Use Retire to remove corporate data and management only.",
            DeviceCommandType.Wipe =>
                "A full wipe is not offered for this management mode. Use Retire to remove management and corporate data.",
            DeviceCommandType.Lock when scenario is { Platform: MobilePlatform.Ios, Mode: ManagementMode.UserEnrollment } =>
                "iOS User Enrollment does not allow the organisation to lock the device.",
            DeviceCommandType.ResetPasscode when scenario.IsPersonalBoundary =>
                "The organisation cannot reset the device passcode on a personally owned device.",
            _ => $"{command} is not supported for {scenario.Platform} {scenario.Mode} ({scenario.Ownership}) devices.",
        };
        throw new UnsupportedOperationException(reason);
    }

    private static Dictionary<ManagementScenario, MdmCapabilitySet> Build()
    {
        var common = new[] { DeviceCommandType.SyncInventory, DeviceCommandType.ApplyPolicy, DeviceCommandType.Retire };

        MdmCapabilitySet Make(
            MobilePlatform platform,
            ManagementMode mode,
            DeviceOwnership ownership,
            IEnumerable<DeviceCommandType> extra,
            bool selective,
            bool wipe,
            bool identifiers,
            bool deviceWide,
            params string[] limitations)
        {
            var scenario = new ManagementScenario(platform, mode, ownership);
            return new MdmCapabilitySet(
                scenario,
                common.Concat(extra).ToHashSet(),
                selective,
                wipe,
                identifiers,
                deviceWide,
                limitations);
        }

        var all = new[]
        {
            Make(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned,
                [DeviceCommandType.Lock, DeviceCommandType.ResetPasscode, DeviceCommandType.Reboot, DeviceCommandType.Wipe],
                selective: false, wipe: true, identifiers: true, deviceWide: true,
                "There is no personal profile: Retire removes management by factory resetting the device.",
                "Reboot requires Android 7.0 or later.",
                "Applications are deployed through the policy, not as individual commands."),
            Make(MobilePlatform.Android, ManagementMode.Dedicated, DeviceOwnership.CompanyOwned,
                [DeviceCommandType.Lock, DeviceCommandType.ResetPasscode, DeviceCommandType.Reboot, DeviceCommandType.Wipe],
                selective: false, wipe: true, identifiers: true, deviceWide: true,
                "Kiosk devices have no personal profile: Retire removes management by factory resetting the device.",
                "Passcode policy usually does not apply to kiosk devices that have no lock screen."),
            Make(MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.CompanyOwned,
                [DeviceCommandType.Lock, DeviceCommandType.ResetPasscode],
                selective: true, wipe: false, identifiers: true, deviceWide: false,
                "Retire relinquishes ownership: the work profile and policies are removed and the personal profile is preserved.",
                "Full wipe is not offered; the personal profile is the user's even on company-owned hardware.",
                "Reboot is not available outside fully managed mode."),
            Make(MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.PersonallyOwned,
                [DeviceCommandType.Lock],
                selective: true, wipe: false, identifiers: false, deviceWide: false,
                "Retire deletes the work profile only; personal apps and data are untouched.",
                "Lock and passcode policy apply to the work profile challenge, not the personal lock screen.",
                "Serial number, IMEI and the personal app inventory are not collected.",
                "Full wipe, passcode reset and reboot are never available."),
            Make(MobilePlatform.Ios, ManagementMode.AutomatedDeviceEnrollment, DeviceOwnership.CompanyOwned,
                [DeviceCommandType.Lock, DeviceCommandType.ResetPasscode, DeviceCommandType.Reboot, DeviceCommandType.Wipe, DeviceCommandType.InstallApp, DeviceCommandType.RemoveApp],
                selective: false, wipe: true, identifiers: true, deviceWide: true,
                "Supervised device: all supervised restrictions and silent app installation are available.",
                "Retire removes the MDM enrollment and managed apps; it is rejected by the device if the enrollment profile was issued as non-removable.",
                "Apple Business Manager synchronisation is not implemented; devices are enrolled through the same check-in protocol."),
            Make(MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned,
                [DeviceCommandType.Lock, DeviceCommandType.ResetPasscode, DeviceCommandType.Wipe, DeviceCommandType.InstallApp, DeviceCommandType.RemoveApp],
                selective: true, wipe: true, identifiers: true, deviceWide: true,
                "Unsupervised device: supervised-only restrictions and silent app installation are not available.",
                "Reboot is only available on supervised devices."),
            Make(MobilePlatform.Ios, ManagementMode.UserEnrollment, DeviceOwnership.PersonallyOwned,
                [DeviceCommandType.InstallApp, DeviceCommandType.RemoveApp],
                selective: true, wipe: false, identifiers: false, deviceWide: false,
                "Only managed apps, managed accounts and managed data are controlled; Retire removes them and nothing else.",
                "UDID, serial number and the personal app inventory are not available to the organisation.",
                "Device lock, passcode clearing, restart and full erase are blocked by the platform.",
                "Device-wide restrictions are ignored; only managed-data boundary restrictions are applied."),
        };

        return all.ToDictionary(s => s.Scenario);
    }
}
