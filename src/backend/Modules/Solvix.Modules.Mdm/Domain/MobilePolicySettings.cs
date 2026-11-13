namespace Solvix.Modules.Mdm.Domain;

/// <summary>
/// Platform-neutral desired state. Provider-specific translators map it to
/// Android Management API policies or Apple configuration profiles, dropping
/// (and reporting) anything the scenario's ownership boundary forbids.
/// Secret values are stored encrypted (<c>enc:</c> envelope) and are never
/// returned by the API.
/// </summary>
public sealed record MobilePolicySettings
{
    public PasswordSettings? Password { get; init; }

    public IReadOnlyList<WifiNetworkSettings> WifiNetworks { get; init; } = [];

    public IReadOnlyList<VpnSettings> Vpn { get; init; } = [];

    public RestrictionSettings Restrictions { get; init; } = new();

    public ComplianceRequirements Compliance { get; init; } = new();

    /// <summary>Dedicated (kiosk) devices only.</summary>
    public KioskSettings? Kiosk { get; init; }

    public AppControlMode AppControl { get; init; } = AppControlMode.BlockListOnly;
}

public enum AppControlMode
{
    /// <summary>Everything is allowed except apps explicitly marked Blocked.</summary>
    BlockListOnly = 1,

    /// <summary>Only apps assigned to the policy may be installed.</summary>
    ApprovedAppsOnly = 2,
}

public enum PasswordQuality
{
    /// <summary>Any lock (pattern, PIN, password).</summary>
    Required = 1,
    Numeric = 2,
    NumericComplex = 3,
    Alphabetic = 4,
    Alphanumeric = 5,
    Complex = 6,
}

public sealed record PasswordSettings
{
    public PasswordQuality Quality { get; init; } = PasswordQuality.Required;

    public int MinLength { get; init; } = 6;

    /// <summary>Failed attempts before the device (company-owned) or work data (BYOD) is wiped. Null = never.</summary>
    public int? MaxFailedAttempts { get; init; }

    /// <summary>Idle seconds before the screen locks.</summary>
    public int? MaxInactivitySeconds { get; init; }

    public int? ExpirationDays { get; init; }

    public int? HistoryLength { get; init; }
}

public enum WifiSecurity
{
    Open = 1,

    /// <summary>WPA2/WPA3 personal (pre-shared key).</summary>
    WpaPersonal = 2,

    /// <summary>WPA2/WPA3 enterprise, PEAP with server-certificate validation.</summary>
    WpaEnterprisePeap = 3,
}

public sealed record WifiNetworkSettings
{
    public string Ssid { get; init; } = string.Empty;

    public WifiSecurity Security { get; init; } = WifiSecurity.WpaPersonal;

    /// <summary>Write-only. Stored encrypted; returned as null with <see cref="PassphraseSet"/>.</summary>
    public string? Passphrase { get; init; }

    public bool PassphraseSet { get; init; }

    public bool AutoJoin { get; init; } = true;

    public bool Hidden { get; init; }

    /// <summary>Enterprise only: PEM certificate of the authority that signed the RADIUS server certificate.</summary>
    public string? ServerCaCertificatePem { get; init; }

    /// <summary>Enterprise only: accepted RADIUS server names.</summary>
    public IReadOnlyList<string> ServerNames { get; init; } = [];

    /// <summary>Enterprise only: outer identity sent before the tunnel is established.</summary>
    public string? AnonymousIdentity { get; init; }
}

public enum VpnType
{
    /// <summary>Android: an always-on VPN app delivered through Managed Google Play.</summary>
    AlwaysOnApp = 1,

    /// <summary>iOS/iPadOS: built-in IKEv2 client.</summary>
    IkeV2 = 2,
}

public sealed record VpnSettings
{
    public string Name { get; init; } = string.Empty;

    public VpnType Type { get; init; }

    /// <summary>AlwaysOnApp: Android package name of the VPN client.</summary>
    public string? PackageName { get; init; }

    /// <summary>AlwaysOnApp: block all traffic while the VPN is down.</summary>
    public bool Lockdown { get; init; }

    /// <summary>IkeV2: gateway host name.</summary>
    public string? Server { get; init; }

    public string? RemoteIdentifier { get; init; }

    public string? LocalIdentifier { get; init; }

    /// <summary>IkeV2: write-only pre-shared key (stored encrypted).</summary>
    public string? SharedSecret { get; init; }

    public bool SharedSecretSet { get; init; }
}

public sealed record RestrictionSettings
{
    public bool DisableCamera { get; init; }

    public bool DisableScreenCapture { get; init; }

    public bool DisableUsbFileTransfer { get; init; }

    /// <summary>Android: block installing apps from outside Google Play. iOS: n/a.</summary>
    public bool DisableUnknownSources { get; init; }

    public bool DisableFactoryReset { get; init; }

    /// <summary>
    /// Stop data moving from the managed/work container into personal apps
    /// (Android cross-profile policies, iOS managed-open-in). Enforced by default
    /// on personally owned devices regardless of this flag.
    /// </summary>
    public bool BlockManagedToPersonalSharing { get; init; }
}

public sealed record ComplianceRequirements
{
    public string? MinimumOsVersion { get; init; }

    public bool RequirePasscode { get; init; }

    public bool RequireEncryption { get; init; }

    public bool BlockCompromisedDevices { get; init; }

    /// <summary>Hours without contact after which the device is non-compliant.</summary>
    public int? MaxHoursSinceCheckIn { get; init; }

    public bool RequireAssignedPolicyApplied { get; init; }
}

public sealed record KioskSettings
{
    /// <summary>Android package names allowed to run on the device. The first Kiosk-type app is the launcher target.</summary>
    public IReadOnlyList<string> AllowedPackages { get; init; } = [];

    public bool DisableStatusBar { get; init; } = true;

    public bool DisableKeyguard { get; init; } = true;

    public bool KeepScreenOn { get; init; } = true;
}
