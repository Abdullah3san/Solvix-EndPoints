namespace Solvix.Modules.Security.Evaluation;

using Solvix.Modules.Security.Domain;

/// <summary>A tenant-maintained advisory used for the vulnerable-software check.</summary>
public sealed record AdvisoryRule(
    Guid Id,
    string Title,
    string ProductPattern,
    string? Publisher,
    string? AffectedFromVersion,
    string FixedVersion,
    Severity Severity,
    string? Reference);

/// <summary>The outcome of one check for one subject (a device-wide check uses an empty subject).</summary>
public sealed record CheckResult(
    string RuleKey,
    string SubjectKey,
    CheckOutcome Outcome,
    Severity Severity,
    string Title,
    string Evidence,
    string? RemediationActionKey = null,
    string? RemediationParameter = null);

/// <summary>
/// Pure, deterministic evaluation of a device report against a policy. No I/O, no clock, no randomness,
/// so identical input always yields identical output and every rule is unit-testable.
///
/// Contract relied on by finding reconciliation:
///  - a rule that fails emits one <see cref="CheckOutcome.Fail"/> per failing subject and nothing else;
///  - a rule that does not fail emits exactly one result with an empty subject
///    (<see cref="CheckOutcome.Pass"/>, <see cref="CheckOutcome.Unknown"/> or <see cref="CheckOutcome.NotApplicable"/>);
///  - when data is missing the rule emits <see cref="CheckOutcome.Unknown"/>, never Pass.
/// </summary>
public static class PostureEvaluator
{
    private const int MaxSoftwareFindings = 100;
    private const int MaxEvidenceLength = 500;

    public static IReadOnlyList<CheckResult> Evaluate(PostureReport report, PolicySettings policy, IReadOnlyList<AdvisoryRule> advisories)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(advisories);

        var results = new List<CheckResult>(48);
        EvaluateDefender(report.Defender, policy, results);
        EvaluateFirewall(report.Firewall, policy, results);
        EvaluateBitLocker(report.BitLocker, policy, results);
        EvaluateAccounts(report.LocalAdmins, policy, results);
        EvaluateUsb(report.Usb, policy, results);
        EvaluateSoftware(report.Software, advisories, results);
        EvaluateConfiguration(report.Configuration, policy, results);
        return results;
    }

    // ------------------------------------------------------------------ Defender

    private static void EvaluateDefender(DefenderStatus? d, PolicySettings policy, List<CheckResult> results)
    {
        string[] keys =
        [
            RuleCatalog.DefenderRealTime, RuleCatalog.DefenderDisabled, RuleCatalog.DefenderSignatures,
            RuleCatalog.DefenderTamper, RuleCatalog.DefenderScan, RuleCatalog.DefenderThreats,
        ];
        if (d is null)
        {
            foreach (var key in keys)
            {
                results.Add(Unknown(key, "Microsoft Defender status was not collected."));
            }

            return;
        }

        var thirdParty = d.ThirdPartyAntivirusRegistered == true;
        var passive = d.AMRunningMode?.Contains("Passive", StringComparison.OrdinalIgnoreCase) == true;
        var defenderIsNotPrimary = thirdParty && (passive || d.AntivirusEnabled == false);

        // DEF-002: is there any active antivirus at all?
        if (d.AntivirusEnabled is null)
        {
            results.Add(Unknown(RuleCatalog.DefenderDisabled, "Defender's enabled state could not be read."));
        }
        else if (d.AntivirusEnabled == false && !thirdParty)
        {
            results.Add(Fail(RuleCatalog.DefenderDisabled, string.Empty, "Microsoft Defender Antivirus is not enabled and Windows Security Center lists no other antivirus product."));
        }
        else
        {
            results.Add(Pass(RuleCatalog.DefenderDisabled, defenderIsNotPrimary
                ? "Another antivirus product is registered with Windows Security Center."
                : "Microsoft Defender Antivirus is enabled."));
        }

        if (defenderIsNotPrimary)
        {
            const string note = "Defender is not the primary antivirus on this device (another product is registered); Defender-specific settings are not enforced.";
            foreach (var key in new[] { RuleCatalog.DefenderRealTime, RuleCatalog.DefenderSignatures, RuleCatalog.DefenderTamper, RuleCatalog.DefenderScan })
            {
                results.Add(NotApplicable(key, note));
            }
        }
        else
        {
            // DEF-001
            if (!policy.RequireDefenderRealTimeProtection)
            {
                results.Add(NotApplicable(RuleCatalog.DefenderRealTime, "The policy does not require real-time protection."));
            }
            else if (d.RealTimeProtectionEnabled is null)
            {
                results.Add(Unknown(RuleCatalog.DefenderRealTime, "Real-time protection state could not be read."));
            }
            else if (d.RealTimeProtectionEnabled == false)
            {
                results.Add(Fail(RuleCatalog.DefenderRealTime, string.Empty, "Real-time protection is disabled."));
            }
            else
            {
                results.Add(Pass(RuleCatalog.DefenderRealTime, "Real-time protection is enabled."));
            }

            // DEF-003
            if (d.SignatureAgeDays is null)
            {
                results.Add(Unknown(RuleCatalog.DefenderSignatures, "Signature age could not be read."));
            }
            else if (d.SignatureAgeDays > policy.MaxSignatureAgeDays)
            {
                results.Add(Fail(RuleCatalog.DefenderSignatures, string.Empty, $"Signatures are {d.SignatureAgeDays} day(s) old; the policy allows {policy.MaxSignatureAgeDays}."));
            }
            else
            {
                results.Add(Pass(RuleCatalog.DefenderSignatures, $"Signatures are {d.SignatureAgeDays} day(s) old (limit {policy.MaxSignatureAgeDays})."));
            }

            // DEF-004
            if (!policy.RequireDefenderTamperProtection)
            {
                results.Add(NotApplicable(RuleCatalog.DefenderTamper, "The policy does not require Tamper Protection."));
            }
            else if (d.TamperProtected is null)
            {
                results.Add(Unknown(RuleCatalog.DefenderTamper, "Tamper Protection state could not be read."));
            }
            else if (d.TamperProtected == false)
            {
                results.Add(Fail(RuleCatalog.DefenderTamper, string.Empty, "Tamper Protection is off."));
            }
            else
            {
                results.Add(Pass(RuleCatalog.DefenderTamper, "Tamper Protection is on."));
            }

            // DEF-005
            if (d.QuickScanAgeDays is null)
            {
                results.Add(Unknown(RuleCatalog.DefenderScan, "Time since the last scan could not be read."));
            }
            else if (d.QuickScanAgeDays > policy.MaxDaysSinceQuickScan)
            {
                results.Add(Fail(RuleCatalog.DefenderScan, string.Empty, $"The last quick scan was {d.QuickScanAgeDays} day(s) ago; the policy allows {policy.MaxDaysSinceQuickScan}."));
            }
            else
            {
                results.Add(Pass(RuleCatalog.DefenderScan, $"The last quick scan was {d.QuickScanAgeDays} day(s) ago."));
            }
        }

        // DEF-006 is evaluated regardless of which antivirus is primary.
        if (d.ActiveThreatCount is null)
        {
            results.Add(Unknown(RuleCatalog.DefenderThreats, "Active threat count could not be read."));
        }
        else if (d.ActiveThreatCount > 0)
        {
            results.Add(Fail(RuleCatalog.DefenderThreats, string.Empty, $"Defender reports {d.ActiveThreatCount} active threat(s) that have not been remediated."));
        }
        else
        {
            results.Add(Pass(RuleCatalog.DefenderThreats, "Defender reports no active threats."));
        }
    }

    // ------------------------------------------------------------------ Firewall

    private static void EvaluateFirewall(FirewallStatus? fw, PolicySettings policy, List<CheckResult> results)
    {
        (string Key, string Profile)[] profiles =
        [
            (RuleCatalog.FirewallDomain, "Domain"),
            (RuleCatalog.FirewallPrivate, "Private"),
            (RuleCatalog.FirewallPublic, "Public"),
        ];

        foreach (var (key, profile) in profiles)
        {
            if (!policy.RequireFirewallAllProfiles)
            {
                results.Add(NotApplicable(key, "The policy does not require the firewall on every profile."));
                continue;
            }

            var found = fw?.Profiles?.FirstOrDefault(p => string.Equals(p.Name, profile, StringComparison.OrdinalIgnoreCase));
            if (found?.Enabled is null)
            {
                results.Add(Unknown(key, $"The {profile} firewall profile state could not be read."));
            }
            else if (found.Enabled == false)
            {
                results.Add(Fail(key, string.Empty, $"The {profile} firewall profile is disabled.", "firewall.enable_profile", profile));
            }
            else
            {
                results.Add(Pass(key, $"The {profile} firewall profile is enabled."));
            }
        }

        if (!policy.RequireFirewallAllProfiles)
        {
            results.Add(NotApplicable(RuleCatalog.FirewallInboundAllow, "The policy does not require the firewall on every profile."));
            return;
        }

        if (fw?.Profiles is null)
        {
            results.Add(Unknown(RuleCatalog.FirewallInboundAllow, "Firewall profiles were not collected."));
            return;
        }

        var permissive = fw.Profiles
            .Where(p => p.Enabled == true && string.Equals(p.DefaultInboundAction, "Allow", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (permissive.Count == 0)
        {
            results.Add(Pass(RuleCatalog.FirewallInboundAllow, "No enabled firewall profile allows inbound traffic by default."));
            return;
        }

        foreach (var profile in permissive)
        {
            results.Add(Fail(RuleCatalog.FirewallInboundAllow, profile.Name, $"The {profile.Name} profile's default inbound action is Allow."));
        }
    }

    // ------------------------------------------------------------------ BitLocker

    private static void EvaluateBitLocker(BitLockerStatus? bl, PolicySettings policy, List<CheckResult> results)
    {
        if (bl is null)
        {
            results.Add(Unknown(RuleCatalog.BitLockerOs, "BitLocker status was not collected."));
            results.Add(policy.RequireBitLockerFixedVolumes
                ? Unknown(RuleCatalog.BitLockerFixed, "BitLocker status was not collected.")
                : NotApplicable(RuleCatalog.BitLockerFixed, "The policy does not require encryption of fixed data drives."));
            results.Add(Unknown(RuleCatalog.BitLockerSuspended, "BitLocker status was not collected."));
            return;
        }

        if (bl.Available == false)
        {
            // The device cannot meet an encryption requirement at all: that is a finding, not a pass.
            if (policy.RequireBitLockerOsVolume)
            {
                results.Add(Fail(RuleCatalog.BitLockerOs, string.Empty, "BitLocker is not available on this edition of Windows, so the system drive cannot be protected by it."));
            }
            else
            {
                results.Add(NotApplicable(RuleCatalog.BitLockerOs, "The policy does not require system-drive encryption."));
            }

            results.Add(NotApplicable(RuleCatalog.BitLockerFixed, "BitLocker is not available on this edition of Windows."));
            results.Add(NotApplicable(RuleCatalog.BitLockerSuspended, "BitLocker is not available on this edition of Windows."));
            return;
        }

        if (bl.Volumes is null)
        {
            results.Add(Unknown(RuleCatalog.BitLockerOs, "BitLocker volumes could not be read."));
            results.Add(policy.RequireBitLockerFixedVolumes
                ? Unknown(RuleCatalog.BitLockerFixed, "BitLocker volumes could not be read.")
                : NotApplicable(RuleCatalog.BitLockerFixed, "The policy does not require encryption of fixed data drives."));
            results.Add(Unknown(RuleCatalog.BitLockerSuspended, "BitLocker volumes could not be read."));
            return;
        }

        var suspended = new List<CheckResult>();

        // BL-001: system volume
        var os = bl.Volumes.FirstOrDefault(v => string.Equals(v.VolumeType, "OperatingSystem", StringComparison.OrdinalIgnoreCase));
        if (!policy.RequireBitLockerOsVolume)
        {
            results.Add(NotApplicable(RuleCatalog.BitLockerOs, "The policy does not require system-drive encryption."));
        }
        else if (os is null || os.ProtectionOn is null)
        {
            results.Add(Unknown(RuleCatalog.BitLockerOs, "The system volume's BitLocker state could not be read."));
        }
        else if (IsSuspended(os))
        {
            results.Add(Pass(RuleCatalog.BitLockerOs, $"{os.MountPoint} is encrypted (protection is suspended; see {RuleCatalog.BitLockerSuspended})."));
        }
        else if (os.ProtectionOn == false)
        {
            results.Add(Fail(RuleCatalog.BitLockerOs, string.Empty, $"{os.MountPoint} is not protected by BitLocker ({DescribeVolume(os)})."));
        }
        else
        {
            results.Add(Pass(RuleCatalog.BitLockerOs, $"{os.MountPoint} is protected by BitLocker ({os.EncryptionMethod ?? "method not reported"})."));
        }

        // BL-002: fixed data volumes
        var fixedVolumes = bl.Volumes.Where(v => string.Equals(v.VolumeType, "Fixed", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!policy.RequireBitLockerFixedVolumes)
        {
            results.Add(NotApplicable(RuleCatalog.BitLockerFixed, "The policy does not require encryption of fixed data drives."));
        }
        else if (fixedVolumes.Any(v => v.ProtectionOn is null))
        {
            results.Add(Unknown(RuleCatalog.BitLockerFixed, "A fixed drive's BitLocker state could not be read."));
        }
        else
        {
            var unprotected = fixedVolumes.Where(v => v.ProtectionOn == false && !IsSuspended(v)).ToList();
            if (unprotected.Count == 0)
            {
                results.Add(Pass(RuleCatalog.BitLockerFixed, fixedVolumes.Count == 0 ? "No fixed data drives present." : "All fixed data drives are protected."));
            }

            foreach (var v in unprotected)
            {
                results.Add(Fail(RuleCatalog.BitLockerFixed, v.MountPoint, $"{v.MountPoint} is not protected by BitLocker ({DescribeVolume(v)})."));
            }
        }

        // BL-003: suspended (any encrypted volume class the policy cares about)
        foreach (var v in bl.Volumes.Where(IsSuspended))
        {
            var relevant = string.Equals(v.VolumeType, "OperatingSystem", StringComparison.OrdinalIgnoreCase)
                ? policy.RequireBitLockerOsVolume
                : string.Equals(v.VolumeType, "Fixed", StringComparison.OrdinalIgnoreCase) && policy.RequireBitLockerFixedVolumes;
            if (relevant)
            {
                suspended.Add(Fail(RuleCatalog.BitLockerSuspended, v.MountPoint, $"BitLocker protection on {v.MountPoint} is suspended; the volume key is exposed until it resumes."));
            }
        }

        if (suspended.Count == 0)
        {
            results.Add(Pass(RuleCatalog.BitLockerSuspended, "No BitLocker protection is suspended."));
        }
        else
        {
            results.AddRange(suspended);
        }
    }

    private static bool IsSuspended(BitLockerVolume v) =>
        v.ProtectionOn == false && string.Equals(v.VolumeStatus, "FullyEncrypted", StringComparison.OrdinalIgnoreCase);

    private static string DescribeVolume(BitLockerVolume v) =>
        $"{v.VolumeStatus ?? "status unknown"}{(v.EncryptionPercentage is { } p ? $", {p}% encrypted" : string.Empty)}";

    // ------------------------------------------------------------------ Accounts

    private static void EvaluateAccounts(LocalAdminStatus? admins, PolicySettings policy, List<CheckResult> results)
    {
        var members = admins?.Members;

        // ADM-001
        if (members is null)
        {
            results.Add(Unknown(RuleCatalog.AdminCount, "Members of the local Administrators group could not be read."));
        }
        else if (members.Count > policy.MaxLocalAdministrators)
        {
            results.Add(Fail(RuleCatalog.AdminCount, string.Empty, $"{members.Count} accounts are local administrators; the policy allows {policy.MaxLocalAdministrators}."));
        }
        else
        {
            results.Add(Pass(RuleCatalog.AdminCount, $"{members.Count} local administrator(s); the policy allows {policy.MaxLocalAdministrators}."));
        }

        // ADM-002
        if (policy.ApprovedLocalAdministrators.Count == 0)
        {
            results.Add(NotApplicable(RuleCatalog.AdminUnapproved, "The policy has no approved-administrator list, so membership is not checked against one."));
        }
        else if (members is null)
        {
            results.Add(Unknown(RuleCatalog.AdminUnapproved, "Members of the local Administrators group could not be read."));
        }
        else
        {
            var approved = new HashSet<string>(policy.ApprovedLocalAdministrators.Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
            var unapproved = members
                .Where(m => !approved.Contains(m.Name.Trim()) && (m.Sid is null || !approved.Contains(m.Sid)))
                .ToList();
            if (unapproved.Count == 0)
            {
                results.Add(Pass(RuleCatalog.AdminUnapproved, "Every local administrator is on the approved list."));
            }

            foreach (var m in unapproved)
            {
                results.Add(Fail(RuleCatalog.AdminUnapproved, m.Name, $"'{m.Name}' ({m.ObjectClass ?? "unknown type"}) is a local administrator and is not on the approved list."));
            }
        }

        // ADM-003
        if (!policy.RequireBuiltInAdministratorDisabled)
        {
            results.Add(NotApplicable(RuleCatalog.AdminBuiltIn, "The policy does not require the built-in Administrator to be disabled."));
        }
        else if (admins?.BuiltInAdministratorEnabled is null)
        {
            results.Add(Unknown(RuleCatalog.AdminBuiltIn, "The built-in Administrator account state could not be read."));
        }
        else if (admins.BuiltInAdministratorEnabled == true)
        {
            results.Add(Fail(RuleCatalog.AdminBuiltIn, string.Empty, "The built-in Administrator account is enabled."));
        }
        else
        {
            results.Add(Pass(RuleCatalog.AdminBuiltIn, "The built-in Administrator account is disabled."));
        }

        // ADM-004
        if (!policy.RequireGuestAccountDisabled)
        {
            results.Add(NotApplicable(RuleCatalog.GuestAccount, "The policy does not require the Guest account to be disabled."));
        }
        else if (admins?.GuestAccountEnabled is null)
        {
            results.Add(Unknown(RuleCatalog.GuestAccount, "The Guest account state could not be read."));
        }
        else if (admins.GuestAccountEnabled == true)
        {
            results.Add(Fail(RuleCatalog.GuestAccount, string.Empty, "The Guest account is enabled."));
        }
        else
        {
            results.Add(Pass(RuleCatalog.GuestAccount, "The Guest account is disabled."));
        }
    }

    // ------------------------------------------------------------------ USB

    public static UsbStoragePolicy? EffectiveUsbPolicy(UsbStatus? usb)
    {
        if (usb is null)
        {
            return null;
        }

        if (usb.StorageDriverDisabled == true || usb.DenyRead == true)
        {
            return UsbStoragePolicy.BlockAll;
        }

        if (usb.DenyWrite == true)
        {
            return UsbStoragePolicy.BlockWrite;
        }

        // Nothing is blocking; only conclude "allowed" when every signal was actually readable.
        return usb.StorageDriverDisabled is not null && usb.DenyRead is not null && usb.DenyWrite is not null
            ? UsbStoragePolicy.Allow
            : null;
    }

    private static void EvaluateUsb(UsbStatus? usb, PolicySettings policy, List<CheckResult> results)
    {
        if (policy.UsbStorage == UsbStoragePolicy.Allow)
        {
            results.Add(NotApplicable(RuleCatalog.UsbStorage, "The policy allows USB storage."));
            return;
        }

        var effective = EffectiveUsbPolicy(usb);
        if (effective is null)
        {
            results.Add(Unknown(RuleCatalog.UsbStorage, "The effective USB storage restriction could not be determined."));
        }
        else if (effective < policy.UsbStorage)
        {
            results.Add(Fail(
                RuleCatalog.UsbStorage,
                string.Empty,
                $"USB storage is effectively '{effective}'; the policy requires at least '{policy.UsbStorage}'.",
                "usb.apply_storage_policy",
                policy.UsbStorage.ToString()));
        }
        else
        {
            results.Add(Pass(RuleCatalog.UsbStorage, $"USB storage is effectively '{effective}' (policy requires at least '{policy.UsbStorage}')."));
        }
    }

    // ------------------------------------------------------------------ Software

    private static void EvaluateSoftware(IReadOnlyList<InstalledSoftware>? software, IReadOnlyList<AdvisoryRule> advisories, List<CheckResult> results)
    {
        if (advisories.Count == 0)
        {
            results.Add(NotApplicable(RuleCatalog.VulnerableSoftware, "No vulnerability advisories are configured, so installed software is not checked."));
            return;
        }

        if (software is null)
        {
            results.Add(Unknown(RuleCatalog.VulnerableSoftware, "The installed software list was not collected."));
            return;
        }

        var failures = new List<CheckResult>();
        foreach (var advisory in advisories)
        {
            if (!VersionComparer.TryParse(advisory.FixedVersion, out var fixedVersion))
            {
                continue;
            }

            var hasFrom = VersionComparer.TryParse(advisory.AffectedFromVersion, out var fromVersion);
            foreach (var item in software)
            {
                if (failures.Count >= MaxSoftwareFindings)
                {
                    break;
                }

                if (!Matches(advisory, item) || !VersionComparer.TryParse(item.Version, out var installed))
                {
                    continue;
                }

                if (VersionComparer.Compare(installed, fixedVersion) >= 0 || (hasFrom && VersionComparer.Compare(installed, fromVersion) < 0))
                {
                    continue;
                }

                var subject = Truncate($"{advisory.Id:N}|{item.Name.Trim().ToLowerInvariant()}", 200);
                var reference = string.IsNullOrWhiteSpace(advisory.Reference) ? string.Empty : $" Reference: {advisory.Reference}.";
                failures.Add(new CheckResult(
                    RuleCatalog.VulnerableSoftware,
                    subject,
                    CheckOutcome.Fail,
                    advisory.Severity,
                    $"{advisory.Title}: {item.Name.Trim()} {item.Version}",
                    Truncate($"Installed version {item.Version} is older than the fixed version {advisory.FixedVersion}.{reference}", MaxEvidenceLength)));
            }
        }

        if (failures.Count == 0)
        {
            results.Add(Pass(RuleCatalog.VulnerableSoftware, $"No installed product matches the {advisories.Count} configured advisories."));
        }
        else
        {
            results.AddRange(failures);
        }
    }

    private static bool Matches(AdvisoryRule advisory, InstalledSoftware item) =>
        !string.IsNullOrWhiteSpace(item.Name)
        && item.Name.Contains(advisory.ProductPattern, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(advisory.Publisher)
            || item.Publisher?.Contains(advisory.Publisher, StringComparison.OrdinalIgnoreCase) == true);

    // ------------------------------------------------------------------ Configuration

    private static void EvaluateConfiguration(ConfigurationStatus? c, PolicySettings policy, List<CheckResult> results)
    {
        // CFG-001
        results.Add(Toggle(
            RuleCatalog.Smb1, policy.RequireSmb1Disabled, c is null ? null : c.Smb1ServerEnabled is null ? null : !c.Smb1ServerEnabled,
            "SMBv1 is disabled.", "The SMBv1 server is enabled.", "The SMBv1 server state could not be read.",
            "The policy does not require SMBv1 to be disabled.", "config.disable_smb1", null));

        // CFG-002
        results.Add(Toggle(
            RuleCatalog.Uac, policy.RequireUac, c?.UacEnabled,
            "User Account Control is enabled.", "User Account Control (EnableLUA) is disabled.", "The UAC state could not be read.",
            "The policy does not require UAC.", null, null));

        // CFG-003
        if (c is null || c.RdpEnabled is null)
        {
            results.Add(policy.RequireRdpNla
                ? Unknown(RuleCatalog.RdpNla, "The Remote Desktop state could not be read.")
                : NotApplicable(RuleCatalog.RdpNla, "The policy does not require NLA."));
        }
        else if (!policy.RequireRdpNla)
        {
            results.Add(NotApplicable(RuleCatalog.RdpNla, "The policy does not require NLA."));
        }
        else if (c.RdpEnabled == false)
        {
            results.Add(Pass(RuleCatalog.RdpNla, "Remote Desktop is disabled."));
        }
        else if (c.RdpNlaRequired is null)
        {
            results.Add(Unknown(RuleCatalog.RdpNla, "Remote Desktop is enabled and its NLA setting could not be read."));
        }
        else if (c.RdpNlaRequired == false)
        {
            results.Add(Fail(RuleCatalog.RdpNla, string.Empty, "Remote Desktop is enabled and Network Level Authentication is not required."));
        }
        else
        {
            results.Add(Pass(RuleCatalog.RdpNla, "Remote Desktop requires Network Level Authentication."));
        }

        // CFG-004
        results.Add(Toggle(
            RuleCatalog.SecureBoot, policy.RequireSecureBoot, c?.SecureBootEnabled,
            "Secure Boot is enabled.", "Secure Boot is disabled.", "The Secure Boot state could not be read (the device may use legacy BIOS boot).",
            "The policy does not require Secure Boot.", null, null));

        // CFG-005
        results.Add(Toggle(
            RuleCatalog.WindowsUpdate, policy.RequireWindowsUpdateNotDisabled, c is null ? null : c.WindowsUpdateDisabledByPolicy is null ? null : !c.WindowsUpdateDisabledByPolicy,
            "Windows Update is not disabled by policy.", "Windows Update is disabled by policy (NoAutoUpdate).", "The Windows Update policy state could not be read.",
            "The policy does not require automatic updates.", null, null));
    }

    /// <summary>Evaluates a "good when true" setting.</summary>
    private static CheckResult Toggle(
        string key, bool required, bool? isGood, string passText, string failText, string unknownText, string naText, string? action, string? actionParameter)
    {
        if (!required)
        {
            return NotApplicable(key, naText);
        }

        return isGood switch
        {
            null => Unknown(key, unknownText),
            true => Pass(key, passText),
            false => Fail(key, string.Empty, failText, action, actionParameter),
        };
    }

    // ------------------------------------------------------------------ helpers

    private static CheckResult Pass(string key, string evidence) => Result(key, string.Empty, CheckOutcome.Pass, evidence, null, null);

    private static CheckResult Unknown(string key, string evidence) => Result(key, string.Empty, CheckOutcome.Unknown, evidence, null, null);

    private static CheckResult NotApplicable(string key, string evidence) => Result(key, string.Empty, CheckOutcome.NotApplicable, evidence, null, null);

    private static CheckResult Fail(string key, string subject, string evidence, string? action = null, string? actionParameter = null) =>
        Result(key, subject, CheckOutcome.Fail, evidence, action, actionParameter);

    private static CheckResult Result(string key, string subject, CheckOutcome outcome, string evidence, string? action, string? actionParameter)
    {
        var rule = RuleCatalog.Get(key);
        return new CheckResult(
            key,
            subject,
            outcome,
            rule.DefaultSeverity,
            rule.Title,
            Truncate(Sanitize(evidence), MaxEvidenceLength),
            outcome == CheckOutcome.Fail ? action ?? rule.RemediationActionKey : null,
            outcome == CheckOutcome.Fail ? actionParameter : null);
    }

    /// <summary>Strips control characters from device-supplied text before it is stored or shown.</summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");

}
