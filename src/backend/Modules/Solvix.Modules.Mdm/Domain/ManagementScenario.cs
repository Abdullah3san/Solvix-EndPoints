namespace Solvix.Modules.Mdm.Domain;

using Solvix.SharedKernel.Exceptions;

/// <summary>
/// A valid combination of platform, management mode and ownership. Policies,
/// enrollment tokens and devices all carry one; a policy can only be assigned
/// to a device of exactly the same scenario, so a kiosk policy can never reach
/// a BYOD work profile.
/// </summary>
public readonly record struct ManagementScenario(MobilePlatform Platform, ManagementMode Mode, DeviceOwnership Ownership)
{
    private static readonly ManagementScenario[] ValidScenarios =
    [
        new(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned),
        new(MobilePlatform.Android, ManagementMode.Dedicated, DeviceOwnership.CompanyOwned),
        new(MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.CompanyOwned),
        new(MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.PersonallyOwned),
        new(MobilePlatform.Ios, ManagementMode.AutomatedDeviceEnrollment, DeviceOwnership.CompanyOwned),
        new(MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned),
        new(MobilePlatform.Ios, ManagementMode.UserEnrollment, DeviceOwnership.PersonallyOwned),
    ];

    public static IReadOnlyList<ManagementScenario> All => ValidScenarios;

    public bool IsValid => ValidScenarios.Contains(this);

    /// <summary>True when the organisation must not touch personal data on this device.</summary>
    public bool IsPersonalBoundary => Ownership == DeviceOwnership.PersonallyOwned;

    public EnrollmentMethod DefaultEnrollmentMethod => Platform switch
    {
        MobilePlatform.Android => EnrollmentMethod.AndroidEnrollmentToken,
        _ => Mode switch
        {
            ManagementMode.UserEnrollment => EnrollmentMethod.AppleUserEnrollment,
            ManagementMode.AutomatedDeviceEnrollment => EnrollmentMethod.AppleAutomated,
            _ => EnrollmentMethod.AppleProfile,
        },
    };

    /// <summary>Throws a validation error explaining why the combination is unsupported.</summary>
    public ManagementScenario EnsureValid()
    {
        if (IsValid)
        {
            return this;
        }

        var reason = (Platform, Mode, Ownership) switch
        {
            (MobilePlatform.Android, ManagementMode.FullyManaged or ManagementMode.Dedicated, DeviceOwnership.PersonallyOwned) =>
                "Fully managed and dedicated Android devices are company-owned by definition; use a work profile for personal devices.",
            (MobilePlatform.Ios, ManagementMode.UserEnrollment, DeviceOwnership.CompanyOwned) =>
                "User Enrollment is the Apple BYOD mode and is only available for personally owned devices.",
            (MobilePlatform.Ios, ManagementMode.AutomatedDeviceEnrollment, DeviceOwnership.PersonallyOwned) =>
                "Automated Device Enrollment is only available for organisation-owned devices.",
            (MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.PersonallyOwned) =>
                "Personally owned iOS/iPadOS devices must use User Enrollment; full device enrollment would give the organisation control over personal data.",
            _ => $"{Mode} is not a valid management mode for {Platform}.",
        };
        throw new ValidationException("scenario", reason);
    }
}
