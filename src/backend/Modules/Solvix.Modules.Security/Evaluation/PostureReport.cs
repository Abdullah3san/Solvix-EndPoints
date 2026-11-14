namespace Solvix.Modules.Security.Evaluation;

/// <summary>
/// What a Solvix collector reports about one Windows device. Every collected fact is
/// nullable on purpose: <c>null</c> means "could not be determined" and is evaluated as
/// <see cref="Domain.CheckOutcome.Unknown"/>, never as a pass.
///
/// Each section maps to a supported Windows interface (see docs/PHASE-10-SECURITY.md):
/// Defender → <c>Get-MpComputerStatus</c>/<c>Get-MpThreat</c>; Firewall → <c>Get-NetFirewallProfile</c>;
/// BitLocker → <c>Get-BitLockerVolume</c>; local admins → <c>Get-LocalGroupMember</c>;
/// USB → documented Removable Storage Access policy keys; software → Uninstall registry keys;
/// configuration → documented registry/CIM/SMB cmdlets; events → <c>Get-WinEvent</c>.
/// </summary>
public sealed record PostureReport
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxSoftwareItems = 5000;
    public const int MaxAdminMembers = 200;
    public const int MaxVolumes = 32;
    public const int MaxEvents = 500;
    public const int MaxErrors = 50;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset CollectedAtUtc { get; init; }

    public string? AgentVersion { get; init; }

    public string? Hostname { get; init; }

    public string? OsCaption { get; init; }

    public string? OsVersion { get; init; }

    public bool? IsDomainJoined { get; init; }

    public DefenderStatus? Defender { get; init; }

    public FirewallStatus? Firewall { get; init; }

    public BitLockerStatus? BitLocker { get; init; }

    public LocalAdminStatus? LocalAdmins { get; init; }

    public UsbStatus? Usb { get; init; }

    public IReadOnlyList<InstalledSoftware>? Software { get; init; }

    public ConfigurationStatus? Configuration { get; init; }

    public IReadOnlyList<ReportedEvent>? Events { get; init; }

    /// <summary>Collection problems the agent hit (section → message). Surfaces as Unknown checks, not as passes.</summary>
    public IReadOnlyList<CollectionError>? Errors { get; init; }
}

public sealed record CollectionError(string Section, string Message);

public sealed record DefenderStatus
{
    /// <summary>"Normal", "Passive", "EDR Block Mode", "SxS Passive Mode", or null when unknown.</summary>
    public string? AMRunningMode { get; init; }

    public bool? AntivirusEnabled { get; init; }

    public bool? RealTimeProtectionEnabled { get; init; }

    public bool? TamperProtected { get; init; }

    public int? SignatureAgeDays { get; init; }

    public int? QuickScanAgeDays { get; init; }

    public int? ActiveThreatCount { get; init; }

    public string? SignatureVersion { get; init; }

    /// <summary>True when Windows Security Center lists another registered antivirus product.</summary>
    public bool? ThirdPartyAntivirusRegistered { get; init; }
}

public sealed record FirewallStatus
{
    public IReadOnlyList<FirewallProfile>? Profiles { get; init; }
}

public sealed record FirewallProfile(string Name, bool? Enabled, string? DefaultInboundAction);

public sealed record BitLockerStatus
{
    /// <summary>False on editions that do not include BitLocker (for example Windows Home).</summary>
    public bool? Available { get; init; }

    public bool? TpmPresent { get; init; }

    public IReadOnlyList<BitLockerVolume>? Volumes { get; init; }
}

/// <param name="MountPoint">Drive letter such as "C:".</param>
/// <param name="VolumeType">"OperatingSystem", "Fixed" or "Removable".</param>
/// <param name="ProtectionOn">Protection status; false when suspended or off, null when unknown.</param>
public sealed record BitLockerVolume(string MountPoint, string? VolumeType, bool? ProtectionOn, int? EncryptionPercentage, string? EncryptionMethod, string? VolumeStatus);

public sealed record LocalAdminStatus
{
    public IReadOnlyList<LocalAdminMember>? Members { get; init; }

    public bool? BuiltInAdministratorEnabled { get; init; }

    public bool? GuestAccountEnabled { get; init; }
}

/// <param name="Name">As reported by Windows, e.g. "CONTOSO\\alice" or "PC01\\Administrator".</param>
/// <param name="ObjectClass">"User", "Group" or "Unknown".</param>
/// <param name="Sid">Security identifier, used to recognise the built-in Administrator (RID 500).</param>
public sealed record LocalAdminMember(string Name, string? ObjectClass, string? Sid);

public sealed record UsbStatus
{
    /// <summary>The USBSTOR service start type is Disabled (4).</summary>
    public bool? StorageDriverDisabled { get; init; }

    /// <summary>Removable Storage Access policy "Removable Disks: Deny write access".</summary>
    public bool? DenyWrite { get; init; }

    /// <summary>Removable Storage Access policy "Removable Disks: Deny read access".</summary>
    public bool? DenyRead { get; init; }
}

public sealed record InstalledSoftware(string Name, string? Version, string? Publisher);

public sealed record ConfigurationStatus
{
    public bool? Smb1ServerEnabled { get; init; }

    public bool? UacEnabled { get; init; }

    public bool? RdpEnabled { get; init; }

    public bool? RdpNlaRequired { get; init; }

    public bool? SecureBootEnabled { get; init; }

    public bool? WindowsUpdateDisabledByPolicy { get; init; }
}

/// <summary>
/// A Windows event the agent chose to forward. Only allow-listed event ids are accepted
/// (<see cref="EventRules.AllowedEventIds"/>); message bodies are reduced to a short summary.
/// </summary>
public sealed record ReportedEvent
{
    public string Log { get; init; } = string.Empty;

    public long RecordId { get; init; }

    public int EventId { get; init; }

    public string? Provider { get; init; }

    public DateTimeOffset TimeCreatedUtc { get; init; }

    public string? Summary { get; init; }

    /// <summary>For group-membership events: the group that changed (e.g. "Administrators").</summary>
    public string? TargetGroup { get; init; }
}
