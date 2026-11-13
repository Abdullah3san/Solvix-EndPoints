namespace Solvix.Endpoint.Contracts.Configuration;

using System.Text.Json.Serialization;

/// <summary>The configuration areas Solvix can manage on a Windows endpoint.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingKind>))]
public enum SettingKind
{
    Service = 1,
    Registry = 2,
    StartupApp = 3,
    FirewallProfile = 4,
    FirewallRule = 5,
    Power = 6,
    ScreenLock = 7,
    UsbStorage = 8,
}

/// <summary>
/// How dangerous applying a setting is. The value is computed from the setting
/// itself (never supplied by a client) by <see cref="SettingRiskClassifier"/>, on
/// the server when a policy version is approved and again on the agent before it
/// changes anything.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingRisk>))]
public enum SettingRisk
{
    /// <summary>Hardening or easily reversible change.</summary>
    Low = 0,

    /// <summary>Weakens a control or can disrupt users; needs a second approver.</summary>
    Elevated = 1,

    /// <summary>Deletes data, disables a security control or can cut connectivity; needs Owner approval and explicit acknowledgement.</summary>
    Destructive = 2,

    /// <summary>Would break the operating system or the management channel; can never be applied.</summary>
    Blocked = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServiceStartupType>))]
public enum ServiceStartupType
{
    Automatic = 1,
    AutomaticDelayed = 2,
    Manual = 3,
    Disabled = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServiceRunState>))]
public enum ServiceRunState
{
    Running = 1,
    Stopped = 2,
}

/// <summary>Registry value types supported by <c>Microsoft.Win32.RegistryValueKind</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegistryValueType>))]
public enum RegistryValueType
{
    String = 1,
    ExpandString = 2,
    DWord = 3,
    QWord = 4,
    MultiString = 5,
    Binary = 6,
}

[JsonConverter(typeof(JsonStringEnumConverter<PresenceEnsure>))]
public enum PresenceEnsure
{
    Present = 1,
    Absent = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<StartupLocation>))]
public enum StartupLocation
{
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run.</summary>
    MachineRun = 1,

    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce.</summary>
    MachineRunOnce = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<StartupEnsure>))]
public enum StartupEnsure
{
    /// <summary>The entry exists with the given command and is enabled.</summary>
    Present = 1,

    /// <summary>The entry is kept but disabled through Explorer's StartupApproved key (what Task Manager does).</summary>
    Disabled = 2,

    /// <summary>The entry is deleted.</summary>
    Absent = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter<FirewallProfile>))]
public enum FirewallProfile
{
    Domain = 1,
    Private = 2,
    Public = 4,
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<FirewallProfiles>))]
public enum FirewallProfiles
{
    None = 0,
    Domain = 1,
    Private = 2,
    Public = 4,
    All = Domain | Private | Public,
}

[JsonConverter(typeof(JsonStringEnumConverter<FirewallAction>))]
public enum FirewallAction
{
    Block = 0,
    Allow = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter<FirewallDirection>))]
public enum FirewallDirection
{
    Inbound = 1,
    Outbound = 2,
}

/// <summary>IANA protocol numbers as used by <c>INetFwRule.Protocol</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FirewallProtocol>))]
public enum FirewallProtocol
{
    Icmpv4 = 1,
    Tcp = 6,
    Udp = 17,
    Icmpv6 = 58,
    Any = 256,
}

[JsonConverter(typeof(JsonStringEnumConverter<PowerSettingType>))]
public enum PowerSettingType
{
    /// <summary>SUB_SLEEP / STANDBYIDLE, seconds (0 = never).</summary>
    SleepTimeout = 1,

    /// <summary>SUB_VIDEO / VIDEOIDLE, seconds (0 = never).</summary>
    DisplayTimeout = 2,

    /// <summary>SUB_SLEEP / HIBERNATEIDLE, seconds (0 = never).</summary>
    HibernateTimeout = 3,

    /// <summary>SUB_BUTTONS / LIDACTION: 0 do nothing, 1 sleep, 2 hibernate, 3 shut down.</summary>
    LidCloseAction = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<PowerSource>))]
public enum PowerSource
{
    /// <summary>Plugged in.</summary>
    Ac = 1,

    /// <summary>On battery.</summary>
    Dc = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<ScreenLockControl>))]
public enum ScreenLockControl
{
    /// <summary>
    /// "Interactive logon: Machine inactivity limit" (HKLM\...\Policies\System\InactivityTimeoutSecs),
    /// seconds, 0 = disabled, max 599940.
    /// </summary>
    InactivityTimeoutSeconds = 1,

    /// <summary>Power setting CONSOLELOCK on AC and DC: 1 = require sign-in on wake.</summary>
    RequirePasswordOnWake = 2,

    /// <summary>
    /// "Interactive logon: Do not require CTRL+ALT+DEL" (HKLM\...\Policies\System\DisableCAD):
    /// 1 = users must press CTRL+ALT+DEL to sign in or unlock (secure attention sequence).
    /// </summary>
    RequireCtrlAltDel = 3,
}

/// <summary>Device classes governed by the "Removable Storage Access" administrative template.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsbDeviceClass>))]
public enum UsbDeviceClass
{
    /// <summary>Removable disks, e.g. USB flash drives ({53f5630d-b6bf-11d0-94f2-00a0c91efb8b}).</summary>
    RemovableDisks = 1,

    /// <summary>Windows Portable Devices: phones, cameras, media players.</summary>
    PortableDevices = 2,

    /// <summary>CD and DVD drives ({53f56308-b6bf-11d0-94f2-00a0c91efb8b}).</summary>
    CdDvd = 3,

    /// <summary>"All Removable Storage classes: Deny all access" (Deny_All).</summary>
    AllRemovableStorage = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<UsbAccess>))]
public enum UsbAccess
{
    FullAccess = 1,
    ReadOnly = 2,
    NoAccess = 3,
}

/// <summary>How a configuration assignment is enforced on its devices.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EnforcementMode>))]
public enum EnforcementMode
{
    /// <summary>Evaluate and report drift only; never change the device.</summary>
    Monitor = 1,

    /// <summary>Apply the setting and remediate drift.</summary>
    Enforce = 2,
}

/// <summary>Per-setting outcome reported by the agent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingComplianceStatus>))]
public enum SettingComplianceStatus
{
    /// <summary>Actual state matches the desired state.</summary>
    Compliant = 1,

    /// <summary>Actual state differs (Monitor mode, or Enforce mode outside the maintenance window).</summary>
    Drifted = 2,

    /// <summary>Desired state was applied for the first time.</summary>
    Applied = 3,

    /// <summary>Drift was detected and corrected.</summary>
    Remediated = 4,

    /// <summary>Applying or reading the setting failed.</summary>
    Failed = 5,

    /// <summary>The agent refused the setting because it was not authorised for its risk.</summary>
    Refused = 6,

    /// <summary>The setting does not apply to this device (e.g. no such service).</summary>
    NotApplicable = 7,

    /// <summary>The pre-change value was restored after the setting was removed.</summary>
    Reverted = 8,

    /// <summary>Restoring the pre-change value failed.</summary>
    RevertFailed = 9,

    /// <summary>The setting type cannot be restored (no snapshot or not supported).</summary>
    RevertUnsupported = 10,
}
