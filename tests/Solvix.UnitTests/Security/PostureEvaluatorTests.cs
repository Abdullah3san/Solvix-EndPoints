namespace Solvix.UnitTests.Security;

using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;

public sealed class PostureEvaluatorTests
{
    private static readonly PolicySettings Policy = PolicySettings.Default;

    internal static PostureReport Healthy() => new()
    {
        CollectedAtUtc = DateTimeOffset.UtcNow,
        Defender = new DefenderStatus
        {
            AMRunningMode = "Normal", AntivirusEnabled = true, RealTimeProtectionEnabled = true, TamperProtected = true,
            SignatureAgeDays = 1, QuickScanAgeDays = 2, ActiveThreatCount = 0, ThirdPartyAntivirusRegistered = false,
        },
        Firewall = new FirewallStatus
        {
            Profiles = [new("Domain", true, "Block"), new("Private", true, "Block"), new("Public", true, "Block")],
        },
        BitLocker = new BitLockerStatus
        {
            Available = true, TpmPresent = true,
            Volumes = [new("C:", "OperatingSystem", true, 100, "XtsAes256", "FullyEncrypted")],
        },
        LocalAdmins = new LocalAdminStatus
        {
            Members = [new("PC\\admin", "User", "S-1-5-21-1-1-1-1001")],
            BuiltInAdministratorEnabled = false, GuestAccountEnabled = false,
        },
        Usb = new UsbStatus { StorageDriverDisabled = false, DenyRead = false, DenyWrite = false },
        Software = [new("Contoso Reader", "2.0.0", "Contoso")],
        Configuration = new ConfigurationStatus
        {
            Smb1ServerEnabled = false, UacEnabled = true, RdpEnabled = false, RdpNlaRequired = null,
            SecureBootEnabled = true, WindowsUpdateDisabledByPolicy = false,
        },
    };

    private static CheckResult Single(IReadOnlyList<CheckResult> r, string key) => Assert.Single(r, x => x.RuleKey == key);

    [Fact]
    public void Healthy_device_has_no_failures_and_no_unknowns()
    {
        var results = PostureEvaluator.Evaluate(Healthy(), Policy, []);
        Assert.DoesNotContain(results, r => r.Outcome == CheckOutcome.Fail);
        Assert.DoesNotContain(results, r => r.Outcome == CheckOutcome.Unknown);
    }

    [Fact]
    public void Every_rule_key_emitted_exists_in_the_catalogue()
    {
        foreach (var r in PostureEvaluator.Evaluate(new PostureReport(), Policy, []))
        {
            Assert.True(RuleCatalog.TryGet(r.RuleKey, out _), r.RuleKey);
        }
    }

    [Fact]
    public void Empty_report_is_unknown_never_pass()
    {
        var results = PostureEvaluator.Evaluate(new PostureReport(), Policy, []);
        Assert.DoesNotContain(results, r => r.Outcome == CheckOutcome.Pass);
        Assert.DoesNotContain(results, r => r.Outcome == CheckOutcome.Fail);
        Assert.Contains(results, r => r.Outcome == CheckOutcome.Unknown);
    }

    [Fact]
    public void Realtime_protection_off_fails_as_high_with_remediation()
    {
        var report = Healthy() with { Defender = Healthy().Defender! with { RealTimeProtectionEnabled = false } };
        var r = Single(PostureEvaluator.Evaluate(report, Policy, []), RuleCatalog.DefenderRealTime);
        Assert.Equal(CheckOutcome.Fail, r.Outcome);
        Assert.Equal(Severity.High, r.Severity);
    }

    [Fact]
    public void Passive_defender_with_third_party_av_is_not_applicable()
    {
        var d = Healthy().Defender! with { AMRunningMode = "Passive", RealTimeProtectionEnabled = false, ThirdPartyAntivirusRegistered = true };
        var results = PostureEvaluator.Evaluate(Healthy() with { Defender = d }, Policy, []);
        Assert.Equal(CheckOutcome.NotApplicable, Single(results, RuleCatalog.DefenderRealTime).Outcome);
        Assert.Equal(CheckOutcome.Pass, Single(results, RuleCatalog.DefenderDisabled).Outcome);
    }

    [Fact]
    public void No_antivirus_at_all_fails()
    {
        var d = Healthy().Defender! with { AntivirusEnabled = false, ThirdPartyAntivirusRegistered = false };
        Assert.Equal(CheckOutcome.Fail, Single(PostureEvaluator.Evaluate(Healthy() with { Defender = d }, Policy, []), RuleCatalog.DefenderDisabled).Outcome);
    }

    [Fact]
    public void Active_threats_are_critical()
    {
        var d = Healthy().Defender! with { ActiveThreatCount = 2 };
        var r = Single(PostureEvaluator.Evaluate(Healthy() with { Defender = d }, Policy, []), RuleCatalog.DefenderThreats);
        Assert.Equal(Severity.Critical, r.Severity);
        Assert.Equal(CheckOutcome.Fail, r.Outcome);
    }

    [Fact]
    public void Old_signatures_fail_only_beyond_the_policy_limit()
    {
        var at = Healthy() with { Defender = Healthy().Defender! with { SignatureAgeDays = Policy.MaxSignatureAgeDays } };
        Assert.Equal(CheckOutcome.Pass, Single(PostureEvaluator.Evaluate(at, Policy, []), RuleCatalog.DefenderSignatures).Outcome);
        var over = Healthy() with { Defender = Healthy().Defender! with { SignatureAgeDays = Policy.MaxSignatureAgeDays + 1 } };
        Assert.Equal(CheckOutcome.Fail, Single(PostureEvaluator.Evaluate(over, Policy, []), RuleCatalog.DefenderSignatures).Outcome);
    }

    [Fact]
    public void Disabled_firewall_profile_fails_with_profile_parameter()
    {
        var report = Healthy() with { Firewall = new FirewallStatus { Profiles = [new("Domain", true, "Block"), new("Private", true, "Block"), new("Public", false, "Block")] } };
        var r = Single(PostureEvaluator.Evaluate(report, Policy, []), RuleCatalog.FirewallPublic);
        Assert.Equal(CheckOutcome.Fail, r.Outcome);
        Assert.Equal("firewall.enable_profile", r.RemediationActionKey);
        Assert.Equal("Public", r.RemediationParameter);
    }

    [Fact]
    public void Missing_firewall_profile_is_unknown()
    {
        var report = Healthy() with { Firewall = new FirewallStatus { Profiles = [new("Domain", true, "Block")] } };
        Assert.Equal(CheckOutcome.Unknown, Single(PostureEvaluator.Evaluate(report, Policy, []), RuleCatalog.FirewallPublic).Outcome);
    }

    [Fact]
    public void Unencrypted_system_drive_fails_and_suspended_is_reported_separately()
    {
        var off = Healthy() with { BitLocker = new BitLockerStatus { Available = true, Volumes = [new("C:", "OperatingSystem", false, 0, null, "FullyDecrypted")] } };
        var results = PostureEvaluator.Evaluate(off, Policy, []);
        Assert.Equal(CheckOutcome.Fail, Single(results, RuleCatalog.BitLockerOs).Outcome);

        var suspended = Healthy() with { BitLocker = new BitLockerStatus { Available = true, Volumes = [new("C:", "OperatingSystem", false, 100, "XtsAes256", "FullyEncrypted")] } };
        results = PostureEvaluator.Evaluate(suspended, Policy, []);
        Assert.Equal(CheckOutcome.Pass, Single(results, RuleCatalog.BitLockerOs).Outcome);
        Assert.Equal(CheckOutcome.Fail, Single(results, RuleCatalog.BitLockerSuspended).Outcome);
    }

    [Fact]
    public void Bitlocker_unavailable_on_edition_fails_when_encryption_is_required()
    {
        var report = Healthy() with { BitLocker = new BitLockerStatus { Available = false } };
        Assert.Equal(CheckOutcome.Fail, Single(PostureEvaluator.Evaluate(report, Policy, []), RuleCatalog.BitLockerOs).Outcome);
        var relaxed = Policy with { RequireBitLockerOsVolume = false };
        Assert.Equal(CheckOutcome.NotApplicable, Single(PostureEvaluator.Evaluate(report, relaxed, []), RuleCatalog.BitLockerOs).Outcome);
    }

    [Fact]
    public void Fixed_drives_are_only_checked_when_policy_requires_it()
    {
        var report = Healthy() with
        {
            BitLocker = new BitLockerStatus
            {
                Available = true,
                Volumes = [new("C:", "OperatingSystem", true, 100, "x", "FullyEncrypted"), new("D:", "Fixed", false, 0, null, "FullyDecrypted")],
            },
        };
        Assert.Equal(CheckOutcome.NotApplicable, Single(PostureEvaluator.Evaluate(report, Policy, []), RuleCatalog.BitLockerFixed).Outcome);
        var strict = Policy with { RequireBitLockerFixedVolumes = true };
        var fail = Single(PostureEvaluator.Evaluate(report, strict, []), RuleCatalog.BitLockerFixed);
        Assert.Equal(CheckOutcome.Fail, fail.Outcome);
        Assert.Equal("D:", fail.SubjectKey);
    }

    [Fact]
    public void Administrator_count_and_approved_list_are_enforced()
    {
        var members = Enumerable.Range(1, 5).Select(i => new LocalAdminMember($"PC\\u{i}", "User", $"S-1-5-21-{i}")).ToList();
        var report = Healthy() with { LocalAdmins = Healthy().LocalAdmins! with { Members = members } };
        Assert.Equal(CheckOutcome.Fail, Single(PostureEvaluator.Evaluate(report, Policy, []), RuleCatalog.AdminCount).Outcome);

        // Without an approved list the unapproved check is not applicable (no false positives for every admin).
        Assert.Equal(CheckOutcome.NotApplicable, Single(PostureEvaluator.Evaluate(report, Policy, []), RuleCatalog.AdminUnapproved).Outcome);

        var withList = Policy with { ApprovedLocalAdministrators = ["pc\\U1", "S-1-5-21-2"] };
        var failures = PostureEvaluator.Evaluate(report, withList, []).Where(r => r.RuleKey == RuleCatalog.AdminUnapproved).ToList();
        Assert.Equal(3, failures.Count);
        Assert.All(failures, f => Assert.Equal(CheckOutcome.Fail, f.Outcome));
        Assert.DoesNotContain(failures, f => f.SubjectKey is "PC\\u1" or "PC\\u2");
    }

    [Theory]
    [InlineData(UsbStoragePolicy.BlockWrite, false, false, false, CheckOutcome.Fail)]
    [InlineData(UsbStoragePolicy.BlockWrite, false, false, true, CheckOutcome.Pass)]
    [InlineData(UsbStoragePolicy.BlockWrite, false, true, false, CheckOutcome.Pass)]
    [InlineData(UsbStoragePolicy.BlockAll, false, false, true, CheckOutcome.Fail)]
    [InlineData(UsbStoragePolicy.BlockAll, true, false, false, CheckOutcome.Pass)]
    [InlineData(UsbStoragePolicy.Allow, false, false, false, CheckOutcome.NotApplicable)]
    public void Usb_policy_compares_effective_restriction_with_requirement(UsbStoragePolicy required, bool driverDisabled, bool denyRead, bool denyWrite, CheckOutcome expected)
    {
        var report = Healthy() with { Usb = new UsbStatus { StorageDriverDisabled = driverDisabled, DenyRead = denyRead, DenyWrite = denyWrite } };
        Assert.Equal(expected, Single(PostureEvaluator.Evaluate(report, Policy with { UsbStorage = required }, []), RuleCatalog.UsbStorage).Outcome);
    }

    [Fact]
    public void Usb_with_unreadable_signal_and_no_block_is_unknown()
    {
        var report = Healthy() with { Usb = new UsbStatus { StorageDriverDisabled = false, DenyRead = null, DenyWrite = false } };
        Assert.Equal(CheckOutcome.Unknown, Single(PostureEvaluator.Evaluate(report, Policy with { UsbStorage = UsbStoragePolicy.BlockWrite }, []), RuleCatalog.UsbStorage).Outcome);
    }

    private static AdvisoryRule Advisory(string product = "Contoso Reader", string? from = null, string fixedVersion = "2.5.0", Severity severity = Severity.High) =>
        new(Guid.NewGuid(), "Contoso Reader RCE", product, null, from, fixedVersion, severity, "CVE-0000-0000");

    [Fact]
    public void Software_below_fixed_version_is_flagged_with_the_advisory_severity()
    {
        var results = PostureEvaluator.Evaluate(Healthy(), Policy, [Advisory(severity: Severity.Critical)]);
        var r = Single(results, RuleCatalog.VulnerableSoftware);
        Assert.Equal(CheckOutcome.Fail, r.Outcome);
        Assert.Equal(Severity.Critical, r.Severity);
        Assert.Contains("CVE-0000-0000", r.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.5.0")]
    [InlineData("3.0")]
    public void Software_at_or_above_fixed_version_passes(string installed)
    {
        var report = Healthy() with { Software = [new("Contoso Reader", installed, null)] };
        Assert.Equal(CheckOutcome.Pass, Single(PostureEvaluator.Evaluate(report, Policy, [Advisory()]), RuleCatalog.VulnerableSoftware).Outcome);
    }

    [Fact]
    public void Affected_from_version_bounds_the_range()
    {
        var report = Healthy() with { Software = [new("Contoso Reader", "1.0.0", null)] };
        Assert.Equal(CheckOutcome.Pass, Single(PostureEvaluator.Evaluate(report, Policy, [Advisory(from: "2.0.0")]), RuleCatalog.VulnerableSoftware).Outcome);
    }

    [Fact]
    public void Unparseable_version_is_not_reported_as_vulnerable()
    {
        var report = Healthy() with { Software = [new("Contoso Reader", "latest", null)] };
        Assert.Equal(CheckOutcome.Pass, Single(PostureEvaluator.Evaluate(report, Policy, [Advisory()]), RuleCatalog.VulnerableSoftware).Outcome);
    }

    [Fact]
    public void Without_advisories_software_check_is_not_applicable_rather_than_pass()
    {
        Assert.Equal(CheckOutcome.NotApplicable, Single(PostureEvaluator.Evaluate(Healthy(), Policy, []), RuleCatalog.VulnerableSoftware).Outcome);
    }

    [Fact]
    public void Advisory_pattern_is_plain_text_not_a_regex()
    {
        var report = Healthy() with { Software = [new("Contoso Reader", "1.0", null)] };
        var results = PostureEvaluator.Evaluate(report, Policy, [Advisory(product: ".*")]);
        Assert.Equal(CheckOutcome.Pass, Single(results, RuleCatalog.VulnerableSoftware).Outcome);
    }

    [Fact]
    public void Configuration_weaknesses_fail()
    {
        var report = Healthy() with
        {
            Configuration = new ConfigurationStatus { Smb1ServerEnabled = true, UacEnabled = false, RdpEnabled = true, RdpNlaRequired = false, SecureBootEnabled = true, WindowsUpdateDisabledByPolicy = true },
        };
        var results = PostureEvaluator.Evaluate(report, Policy, []);
        foreach (var key in new[] { RuleCatalog.Smb1, RuleCatalog.Uac, RuleCatalog.RdpNla, RuleCatalog.WindowsUpdate })
        {
            Assert.Equal(CheckOutcome.Fail, Single(results, key).Outcome);
        }

        Assert.Equal("config.disable_smb1", Single(results, RuleCatalog.Smb1).RemediationActionKey);
    }

    [Fact]
    public void Device_supplied_text_is_stripped_of_control_characters()
    {
        Assert.Equal("a b c", PostureEvaluator.Sanitize("a\nb\tc"));
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        var report = Healthy() with { Defender = Healthy().Defender! with { RealTimeProtectionEnabled = false } };
        Assert.Equal(PostureEvaluator.Evaluate(report, Policy, []), PostureEvaluator.Evaluate(report, Policy, []));
    }

    [Fact]
    public void Failing_rules_emit_only_failures_and_passing_rules_exactly_one_result()
    {
        var report = Healthy() with { Defender = Healthy().Defender! with { RealTimeProtectionEnabled = false } };
        var results = PostureEvaluator.Evaluate(report, Policy, []);
        foreach (var group in results.GroupBy(r => r.RuleKey))
        {
            if (group.Any(r => r.Outcome == CheckOutcome.Fail))
            {
                Assert.All(group, r => Assert.Equal(CheckOutcome.Fail, r.Outcome));
            }
            else
            {
                Assert.Single(group);
            }
        }
    }
}
