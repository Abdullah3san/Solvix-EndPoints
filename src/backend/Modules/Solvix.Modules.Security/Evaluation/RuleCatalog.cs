namespace Solvix.Modules.Security.Evaluation;

using Solvix.Modules.Security.Domain;

/// <summary>
/// The explanation of one rule. Findings reference rules by <see cref="Key"/>; the text lives here,
/// not in the database, so improving an explanation never requires re-evaluating devices.
/// </summary>
/// <param name="WhatItChecks">What was inspected, in plain language.</param>
/// <param name="WhyItMatters">The risk when the check fails.</param>
/// <param name="SeverityRationale">Why this rule has this severity under the published rubric.</param>
/// <param name="HowToFix">Manual remediation guidance an administrator can follow.</param>
/// <param name="RemediationActionKey">A <see cref="RemediationCatalog"/> action that can fix it, or null when the fix is manual only.</param>
/// <param name="WindowsInterface">The documented Windows interface the data comes from.</param>
public sealed record RuleDefinition(
    string Key,
    string Title,
    RuleCategory Category,
    Severity DefaultSeverity,
    string WhatItChecks,
    string WhyItMatters,
    string SeverityRationale,
    string HowToFix,
    string? RemediationActionKey,
    string WindowsInterface);

public static class RuleCatalog
{
    public const string DefenderRealTime = "DEF-001";
    public const string DefenderDisabled = "DEF-002";
    public const string DefenderSignatures = "DEF-003";
    public const string DefenderTamper = "DEF-004";
    public const string DefenderScan = "DEF-005";
    public const string DefenderThreats = "DEF-006";
    public const string FirewallDomain = "FW-001";
    public const string FirewallPrivate = "FW-002";
    public const string FirewallPublic = "FW-003";
    public const string FirewallInboundAllow = "FW-004";
    public const string BitLockerOs = "BL-001";
    public const string BitLockerFixed = "BL-002";
    public const string BitLockerSuspended = "BL-003";
    public const string AdminCount = "ADM-001";
    public const string AdminUnapproved = "ADM-002";
    public const string AdminBuiltIn = "ADM-003";
    public const string GuestAccount = "ADM-004";
    public const string UsbStorage = "USB-001";
    public const string VulnerableSoftware = "SW-001";
    public const string Smb1 = "CFG-001";
    public const string Uac = "CFG-002";
    public const string RdpNla = "CFG-003";
    public const string SecureBoot = "CFG-004";
    public const string WindowsUpdate = "CFG-005";

    public const string EventAuditCleared = "EVT-001";
    public const string EventMalwareDetected = "EVT-002";
    public const string EventDefenderDisabled = "EVT-003";
    public const string EventFailedLogons = "EVT-004";
    public const string EventAdminGroupChange = "EVT-005";
    public const string EventAccountCreated = "EVT-006";

    private static readonly RuleDefinition[] Rules =
    [
        new(DefenderRealTime, "Microsoft Defender real-time protection is off", RuleCategory.Defender, Severity.High,
            "Whether Microsoft Defender Antivirus real-time protection is enabled.",
            "With real-time protection off, files are not scanned when opened or run, so malware can execute without being blocked.",
            "High: a primary endpoint protection is disabled.",
            "Open Windows Security > Virus & threat protection > Manage settings and turn on Real-time protection, or remove the Group Policy/Intune setting that disables it. If a third-party antivirus is meant to be active, register it with Windows Security Center.",
            "defender.enable_realtime", "Get-MpComputerStatus (RealTimeProtectionEnabled)"),
        new(DefenderDisabled, "Microsoft Defender Antivirus is not enabled and no other antivirus is registered", RuleCategory.Defender, Severity.High,
            "Whether Defender Antivirus is enabled, or whether Windows Security Center lists another registered antivirus product.",
            "A device with no active antivirus engine has no malware scanning at all.",
            "High: a primary endpoint protection is absent.",
            "Enable Microsoft Defender Antivirus, or install and register a supported antivirus product.",
            null, "Get-MpComputerStatus (AntivirusEnabled, AMRunningMode); Windows Security Center"),
        new(DefenderSignatures, "Microsoft Defender signatures are out of date", RuleCategory.Defender, Severity.Medium,
            "Age of the antivirus signature set compared with the policy limit.",
            "Old signatures cannot recognise recent malware, so protection degrades even though Defender appears to be running.",
            "Medium: protection is degraded but still active.",
            "Check that the device can reach Microsoft Update or your internal update source, then run Update-MpSignature or Windows Update.",
            "defender.update_signatures", "Get-MpComputerStatus (AntivirusSignatureAge)"),
        new(DefenderTamper, "Microsoft Defender Tamper Protection is off", RuleCategory.Defender, Severity.Medium,
            "Whether Tamper Protection is enabled.",
            "Tamper Protection stops malware and other software from turning off Defender settings. Without it, an attacker with admin rights can disable protection silently.",
            "Medium: a hardening control is missing; the primary protection still runs.",
            "Enable Tamper Protection in Windows Security > Virus & threat protection settings, or centrally through Microsoft Intune / Defender for Endpoint (it cannot be set by local scripts by design).",
            null, "Get-MpComputerStatus (IsTamperProtected)"),
        new(DefenderScan, "No recent Microsoft Defender scan", RuleCategory.Defender, Severity.Low,
            "Days since the last quick scan completed.",
            "Regular scans catch dormant malware that real-time protection missed or that arrived while protection was off.",
            "Low: hygiene issue; real-time protection is the primary control.",
            "Run a quick scan (Start-MpScan -ScanType QuickScan) and confirm scheduled scans are not disabled.",
            "defender.quick_scan", "Get-MpComputerStatus (QuickScanAge)"),
        new(DefenderThreats, "Microsoft Defender reports active threats", RuleCategory.Defender, Severity.Critical,
            "Number of threats that Defender detected and has not yet remediated.",
            "An unremediated detection means malware may still be present or running on the device.",
            "Critical: evidence of active compromise.",
            "Review the detections in Windows Security > Protection history, remediate or quarantine them, then run a full scan. Consider isolating the device while you investigate. Solvix reports Defender's findings; it does not detect or remove malware itself.",
            null, "Get-MpThreat"),

        new(FirewallDomain, "Windows Firewall is off for the Domain profile", RuleCategory.Firewall, Severity.Medium,
            "Whether Windows Defender Firewall is enabled on the Domain profile.",
            "Without the firewall, any listening service is reachable from the corporate network, which widens lateral-movement paths.",
            "Medium: the Domain profile applies on the managed network, where other controls usually also exist.",
            "Enable the Domain profile via Group Policy or Set-NetFirewallProfile -Profile Domain -Enabled True.",
            "firewall.enable_profile", "Get-NetFirewallProfile"),
        new(FirewallPrivate, "Windows Firewall is off for the Private profile", RuleCategory.Firewall, Severity.High,
            "Whether Windows Defender Firewall is enabled on the Private profile.",
            "Private networks (home or small office) are less controlled; with the firewall off every listening service is exposed to them.",
            "High: a primary network protection is disabled on a less-trusted network.",
            "Enable the Private profile via Group Policy or Set-NetFirewallProfile -Profile Private -Enabled True.",
            "firewall.enable_profile", "Get-NetFirewallProfile"),
        new(FirewallPublic, "Windows Firewall is off for the Public profile", RuleCategory.Firewall, Severity.High,
            "Whether Windows Defender Firewall is enabled on the Public profile.",
            "Public networks (cafés, hotels) are hostile; with the firewall off any listening service is exposed to strangers.",
            "High: a primary network protection is disabled on the least-trusted network.",
            "Enable the Public profile via Group Policy or Set-NetFirewallProfile -Profile Public -Enabled True.",
            "firewall.enable_profile", "Get-NetFirewallProfile"),
        new(FirewallInboundAllow, "Windows Firewall allows inbound traffic by default", RuleCategory.Firewall, Severity.Medium,
            "Whether an enabled firewall profile has its default inbound action set to Allow.",
            "A firewall that allows inbound traffic unless a rule blocks it offers little protection against unexpected services.",
            "Medium: protection is configured to be permissive.",
            "Set the default inbound action to Block (Set-NetFirewallProfile -DefaultInboundAction Block) and add explicit allow rules for the services you need.",
            null, "Get-NetFirewallProfile (DefaultInboundAction)"),

        new(BitLockerOs, "The system drive is not encrypted with BitLocker", RuleCategory.Encryption, Severity.High,
            "Whether BitLocker protection is on for the operating system volume.",
            "An unencrypted system drive exposes all data, cached credentials and tokens if the device is lost or stolen.",
            "High: a primary data-at-rest protection is absent.",
            "Enable BitLocker on the system drive and escrow the recovery key to Active Directory, Entra ID or another controlled location first. Solvix does not enable BitLocker automatically because doing so without key escrow can cause permanent data loss.",
            null, "Get-BitLockerVolume (ProtectionStatus)"),
        new(BitLockerFixed, "A fixed data drive is not encrypted with BitLocker", RuleCategory.Encryption, Severity.Medium,
            "Whether BitLocker protection is on for fixed (non-system) drives.",
            "Data on an unencrypted secondary drive can be read by anyone with physical access to the disk.",
            "Medium: data-at-rest protection is missing on a secondary volume.",
            "Enable BitLocker on the drive (with recovery-key escrow) or move the data to an encrypted volume.",
            null, "Get-BitLockerVolume (ProtectionStatus)"),
        new(BitLockerSuspended, "BitLocker protection is suspended", RuleCategory.Encryption, Severity.Medium,
            "Whether an encrypted volume has its protection suspended.",
            "While suspended, the volume key is stored in the clear, so the drive is effectively unprotected until protection resumes.",
            "Medium: the control is installed but temporarily not effective.",
            "Resume protection (Resume-BitLocker). Suspension is normal during a BIOS or OS update; if it persists, find out why.",
            null, "Get-BitLockerVolume (ProtectionStatus, VolumeStatus)"),

        new(AdminCount, "Too many local administrators", RuleCategory.LocalAdmins, Severity.Medium,
            "Number of members of the local Administrators group compared with the policy limit.",
            "Every administrator is a high-value account; each one increases the chance that a stolen credential gives full control of the device.",
            "Medium: increases exposure but is not itself a compromise.",
            "Review the Administrators group (Get-LocalGroupMember Administrators) and remove accounts that do not need it. Prefer just-in-time elevation over standing admin rights.",
            null, "Get-LocalGroupMember -Group Administrators"),
        new(AdminUnapproved, "Unapproved local administrator", RuleCategory.LocalAdmins, Severity.High,
            "Whether each member of the local Administrators group appears on the policy's approved list.",
            "An unexpected administrator may be a forgotten account, an attacker's persistence mechanism, or an unsanctioned privilege grant.",
            "High: unreviewed administrative access.",
            "Confirm with the account owner. If it is legitimate, add it to the policy's approved list; otherwise remove it (Remove-LocalGroupMember). Solvix does not remove administrators automatically because that can lock people out.",
            null, "Get-LocalGroupMember -Group Administrators"),
        new(AdminBuiltIn, "The built-in Administrator account is enabled", RuleCategory.LocalAdmins, Severity.Medium,
            "Whether the local built-in Administrator account (RID 500) is enabled.",
            "The built-in account has a well-known name and is exempt from lockout in some configurations, which makes it a common brute-force target.",
            "Medium: weakens account hygiene.",
            "Disable the account (Disable-LocalUser) and use named administrator accounts, or manage it with Windows LAPS.",
            null, "Get-LocalUser"),
        new(GuestAccount, "The Guest account is enabled", RuleCategory.LocalAdmins, Severity.Medium,
            "Whether the local Guest account is enabled.",
            "An enabled Guest account allows access without individual credentials, which defeats accountability.",
            "Medium: weakens account hygiene.",
            "Disable the Guest account (Disable-LocalUser -Name Guest).",
            null, "Get-LocalUser"),

        new(UsbStorage, "USB storage policy is weaker than required", RuleCategory.RemovableMedia, Severity.Medium,
            "The effective removable-storage restriction (allow, block write, block all) compared with the policy.",
            "Unrestricted USB storage is a route for data theft and for introducing malware.",
            "Medium: a data-loss control is not enforced as policy requires.",
            "Apply the Removable Storage Access policy 'Removable Disks: Deny write access' (or deny read) through Group Policy/Intune, or approve the Solvix remediation action.",
            "usb.apply_storage_policy", "Removable Storage Access policy registry values; USBSTOR service start type"),

        new(VulnerableSoftware, "Installed software matches a known-vulnerable version", RuleCategory.Software, Severity.High,
            "Installed products compared with the vulnerability advisories your administrators maintain (product name and version range).",
            "Software with a published vulnerability can be exploited remotely or locally. Matches are only as good as the advisory list: Solvix does not ship or sync a vulnerability database.",
            "Taken from the advisory: the severity your administrators assigned.",
            "Update or uninstall the product to at least the fixed version named in the finding. Solvix does not uninstall or update software automatically.",
            null, "Uninstall registry keys (HKLM/HKCU\\...\\Uninstall)"),

        new(Smb1, "SMBv1 server is enabled", RuleCategory.Configuration, Severity.High,
            "Whether the legacy SMBv1 server protocol is enabled.",
            "SMBv1 is obsolete and was the vector for wormable exploits such as EternalBlue/WannaCry.",
            "High: a well-known, remotely exploitable weakness.",
            "Disable it: Set-SmbServerConfiguration -EnableSMB1Protocol $false, after confirming no legacy device needs it.",
            "config.disable_smb1", "Get-SmbServerConfiguration (EnableSMB1Protocol)"),
        new(Uac, "User Account Control is disabled", RuleCategory.Configuration, Severity.High,
            "Whether UAC (EnableLUA) is turned on.",
            "With UAC off, every program run by an administrator has full privileges with no prompt, so malware runs with maximum rights.",
            "High: a primary privilege-separation control is disabled.",
            "Enable UAC through Group Policy ('User Account Control: Run all administrators in Admin Approval Mode') and restart the device.",
            null, "Registry: HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\\EnableLUA"),
        new(RdpNla, "Remote Desktop is enabled without Network Level Authentication", RuleCategory.Configuration, Severity.High,
            "Whether Remote Desktop is enabled and, if so, whether NLA is required.",
            "Without NLA, the sign-in screen is exposed before authentication, which enables pre-authentication attacks and brute-forcing.",
            "High: a remotely reachable service lacks a basic protection.",
            "Require NLA in System Properties > Remote, or disable Remote Desktop where it is not needed.",
            null, "Win32_TSGeneralSetting (UserAuthenticationRequired); registry fDenyTSConnections"),
        new(SecureBoot, "Secure Boot is disabled", RuleCategory.Configuration, Severity.Medium,
            "Whether UEFI Secure Boot is enabled.",
            "Secure Boot prevents unsigned boot-level malware (bootkits) from loading before the operating system.",
            "Medium: a hardening control is missing.",
            "Enable Secure Boot in the device firmware settings (requires UEFI boot mode).",
            null, "Confirm-SecureBootUEFI"),
        new(WindowsUpdate, "Windows Update is disabled by policy", RuleCategory.Configuration, Severity.Medium,
            "Whether automatic updates are disabled through policy (NoAutoUpdate).",
            "A device that cannot patch itself accumulates known vulnerabilities.",
            "Medium: patching is blocked, which degrades every other control over time.",
            "Remove the policy that disables automatic updates, or confirm the device is patched by another managed channel.",
            null, "Registry: HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU\\NoAutoUpdate"),

        new(EventAuditCleared, "Windows event log was cleared", RuleCategory.Events, Severity.Critical,
            "Windows event 1102 (Security log cleared) or 104 (System log cleared).",
            "Clearing logs is a common step after intrusion to hide activity. It is rarely a normal operation.",
            "Critical: a strong indicator of attacker activity or of tampering.",
            "Find out who cleared the log and why. If it was not planned, treat the device as potentially compromised and preserve evidence.",
            null, "Get-WinEvent (Security 1102, System 104)"),
        new(EventMalwareDetected, "Microsoft Defender detected malware", RuleCategory.Events, Severity.High,
            "Defender operational event 1116 (malware detected).",
            "Defender found malicious or unwanted software. Whether it was removed is shown by the device's Defender status and threat count.",
            "High: malware reached the device.",
            "Review Protection history, confirm the threat was remediated, and find out how it arrived.",
            null, "Get-WinEvent (Microsoft-Windows-Windows Defender/Operational 1116)"),
        new(EventDefenderDisabled, "Defender real-time protection was disabled", RuleCategory.Events, Severity.High,
            "Defender operational event 5001 (real-time protection disabled).",
            "Real-time protection is often switched off by malware or by users; the device is unprotected until it is turned back on.",
            "High: a primary protection was turned off.",
            "Confirm who disabled it, and re-enable it.",
            null, "Get-WinEvent (Microsoft-Windows-Windows Defender/Operational 5001)"),
        new(EventFailedLogons, "Burst of failed sign-ins", RuleCategory.Events, Severity.Medium,
            "Security event 4625 (failed logon) counts within the policy's time window.",
            "Many failed sign-ins in a short time suggest password guessing or credential stuffing against the device.",
            "Medium by default; High when the count is five times the policy threshold.",
            "Check the source of the attempts, lock or reset affected accounts, and make sure the device is not exposed to the internet (RDP/SMB).",
            null, "Get-WinEvent (Security 4625)"),
        new(EventAdminGroupChange, "Account added to the local Administrators group", RuleCategory.Events, Severity.High,
            "Security event 4732 where the group is Administrators.",
            "Adding an administrator is how attackers persist; legitimate changes should be planned and approved.",
            "High: privileged access changed.",
            "Verify the change was authorised. If not, remove the account and investigate how it was added.",
            null, "Get-WinEvent (Security 4732)"),
        new(EventAccountCreated, "Local user account created", RuleCategory.Events, Severity.Medium,
            "Security event 4720 (user account created).",
            "New local accounts that were not provisioned by IT are a persistence technique.",
            "Medium: needs review but is often legitimate.",
            "Verify the account was created intentionally; disable it if not.",
            null, "Get-WinEvent (Security 4720)"),
    ];

    private static readonly Dictionary<string, RuleDefinition> ByKey =
        Rules.ToDictionary(r => r.Key, StringComparer.Ordinal);

    public static IReadOnlyList<RuleDefinition> All => Rules;

    public static RuleDefinition Get(string key) =>
        ByKey.TryGetValue(key, out var rule) ? rule : throw new KeyNotFoundException($"Unknown security rule '{key}'.");

    public static bool TryGet(string key, out RuleDefinition rule) => ByKey.TryGetValue(key, out rule!);
}

/// <summary>The published severity rubric. Served by the API so the UI can explain every severity.</summary>
public sealed record SeverityDescription(Severity Severity, string Meaning, string Example, int ScorePenalty);

public static class SeverityRubric
{
    public static IReadOnlyList<SeverityDescription> All { get; } =
    [
        new(Severity.Critical, "Evidence of active compromise, or exposure to remote exploitation right now.", "Defender reports an unremediated threat; the audit log was cleared.", ComplianceScorer.PenaltyFor(Severity.Critical)),
        new(Severity.High, "A primary protection is disabled or a well-known weakness is exposed.", "Firewall off on the Public profile; system drive not encrypted; SMBv1 enabled.", ComplianceScorer.PenaltyFor(Severity.High)),
        new(Severity.Medium, "A protection is degraded or a policy is not met, but a primary control still works.", "Defender signatures are a week old; too many local administrators.", ComplianceScorer.PenaltyFor(Severity.Medium)),
        new(Severity.Low, "Hygiene issue; exploitation would need several other failures first.", "No Defender scan for two weeks.", ComplianceScorer.PenaltyFor(Severity.Low)),
        new(Severity.Info, "Informational. Never changes a compliance score.", "A section could not be collected.", 0),
    ];
}
