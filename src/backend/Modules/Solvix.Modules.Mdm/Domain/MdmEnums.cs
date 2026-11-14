namespace Solvix.Modules.Mdm.Domain;

public enum MobilePlatform
{
    Android = 1,
    Ios = 2,
}

/// <summary>Who owns the hardware. Drives the personal/corporate data boundary.</summary>
public enum DeviceOwnership
{
    CompanyOwned = 1,

    /// <summary>BYOD. The organisation manages corporate data only and never sees personal data.</summary>
    PersonallyOwned = 2,
}

/// <summary>The platform mechanism used to manage the device.</summary>
public enum ManagementMode
{
    /// <summary>Android Enterprise fully managed (device owner).</summary>
    FullyManaged = 1,

    /// <summary>Android Enterprise dedicated device (kiosk / single-purpose).</summary>
    Dedicated = 2,

    /// <summary>Android Enterprise work profile (BYOD or company-owned with personal use).</summary>
    WorkProfile = 3,

    /// <summary>Apple MDM, device enrollment (profile based).</summary>
    DeviceEnrollment = 10,

    /// <summary>Apple Automated Device Enrollment (Apple Business Manager), supervised.</summary>
    AutomatedDeviceEnrollment = 11,

    /// <summary>Apple User Enrollment (BYOD): managed data only, no device-level control.</summary>
    UserEnrollment = 12,
}

public enum DeviceState
{
    /// <summary>Handle issued and enrollment profile served; the device has not completed enrollment yet.</summary>
    Pending = 0,
    Enrolled = 1,

    /// <summary>A retire/wipe command was dispatched and is awaiting the device.</summary>
    RemovalPending = 2,

    /// <summary>Management (and, on BYOD, only corporate data) was removed.</summary>
    Retired = 3,

    /// <summary>The whole device was factory reset.</summary>
    Wiped = 4,
}

public enum ComplianceState
{
    Unknown = 0,
    Compliant = 1,
    NonCompliant = 2,
}

public enum IntegrityState
{
    Unknown = 0,
    Intact = 1,
    Compromised = 2,
}

public enum DeviceCommandType
{
    SyncInventory = 1,
    ApplyPolicy = 2,
    Lock = 3,
    ResetPasscode = 4,
    Reboot = 5,
    InstallApp = 6,
    RemoveApp = 7,

    /// <summary>Remove management and corporate data. On BYOD this leaves personal data untouched.</summary>
    Retire = 8,

    /// <summary>Factory reset the entire device. Never offered for personally owned devices.</summary>
    Wipe = 9,
}

public enum CommandState
{
    PendingApproval = 1,
    Rejected = 2,
    Queued = 3,
    Sent = 4,
    Acknowledged = 5,
    Failed = 6,
    Expired = 7,
    Cancelled = 8,
}

public enum AppInstallType
{
    /// <summary>Installed automatically and kept installed.</summary>
    ForceInstalled = 1,

    /// <summary>Offered in the managed store; the user decides.</summary>
    Available = 2,

    /// <summary>Must not be installed / is removed where the platform allows.</summary>
    Blocked = 3,

    /// <summary>Android dedicated devices only: the app runs as the kiosk.</summary>
    Kiosk = 4,
}

public enum AppStoreSource
{
    /// <summary>Managed Google Play.</summary>
    ManagedGooglePlay = 1,

    /// <summary>App Store via Apps and Books (VPP) licences.</summary>
    AppStoreVpp = 2,
}

public enum EnrollmentMethod
{
    /// <summary>Android enrollment token delivered as QR code / provisioning string.</summary>
    AndroidEnrollmentToken = 1,

    /// <summary>Apple device enrollment via a downloaded enrollment profile.</summary>
    AppleProfile = 2,

    /// <summary>Apple User Enrollment profile (BYOD).</summary>
    AppleUserEnrollment = 3,

    /// <summary>Apple Automated Device Enrollment (Apple Business Manager).</summary>
    AppleAutomated = 4,
}
