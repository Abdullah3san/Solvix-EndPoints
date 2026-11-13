namespace Solvix.UnitTests.Mdm;

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Solvix.Modules.Mdm;
using Solvix.Modules.Mdm.Application.Compliance;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers;
using Solvix.Modules.Mdm.Providers.Apple;

public sealed class SignatureAndComplianceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = "<plist><dict/></plist>"u8.ToArray();

    // ----------------------------------------------------------- signatures

    private static X509Certificate2 SelfSigned(string name = "CN=device")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private static string Sign(byte[] body, X509Certificate2 identity)
    {
        var cms = new SignedCms(new ContentInfo(body), detached: true);
        cms.ComputeSignature(new CmsSigner(identity) { IncludeOption = X509IncludeOption.EndCertOnly });
        return Convert.ToBase64String(cms.Encode());
    }

    private static AppleDeviceSignatureVerifier Verifier(string? caPem = null, bool allowUntrusted = false) =>
        new(Options.Create(new MdmOptions { Apple = new AppleMdmOptions { IdentityCaCertificatePem = caPem, AllowUntrustedDeviceIdentity = allowUntrusted } }));

    [Fact]
    public void Valid_signature_returns_a_stable_thumbprint_of_the_signer()
    {
        using var identity = SelfSigned();
        var verifier = Verifier(allowUntrusted: true);
        var thumbprint = verifier.Verify(Body, Sign(Body, identity));
        Assert.Equal(Convert.ToHexString(identity.GetCertHash(HashAlgorithmName.SHA256)), thumbprint);
        Assert.Equal(thumbprint, verifier.Verify(Body, Sign(Body, identity)));
        using var other = SelfSigned();
        Assert.NotEqual(thumbprint, verifier.Verify(Body, Sign(Body, other)));
    }

    [Fact]
    public void Missing_malformed_and_tampered_signatures_are_rejected()
    {
        using var identity = SelfSigned();
        var verifier = Verifier(allowUntrusted: true);
        var signature = Sign(Body, identity);
        Assert.Throws<AppleSignatureException>(() => verifier.Verify(Body, null));
        Assert.Throws<AppleSignatureException>(() => verifier.Verify(Body, "  "));
        Assert.Throws<AppleSignatureException>(() => verifier.Verify(Body, "%%%not-base64"));
        Assert.Throws<AppleSignatureException>(() => verifier.Verify(Body, Convert.ToBase64String("garbage"u8.ToArray())));
        Assert.Throws<AppleSignatureException>(() => verifier.Verify("<plist><dict><key>x</key><string>y</string></dict></plist>"u8, signature));
    }

    [Fact]
    public void Without_a_configured_identity_ca_devices_are_only_accepted_in_explicit_development_mode()
    {
        using var identity = SelfSigned();
        var signature = Sign(Body, identity);
        var ex = Assert.Throws<AppleSignatureException>(() => Verifier().Verify(Body, signature));
        Assert.Contains("identity CA", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void With_a_configured_identity_ca_only_certificates_issued_by_it_are_trusted()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Solvix Device CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        using var deviceKey = RSA.Create(2048);
        var deviceRequest = new CertificateRequest("CN=device-1", deviceKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var issued = deviceRequest.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(8));
        using var device = issued.CopyWithPrivateKey(deviceKey);
        using var deviceWithKey = X509CertificateLoader.LoadPkcs12(device.Export(X509ContentType.Pfx), null);

        var verifier = Verifier(ca.ExportCertificatePem());
        Assert.Equal(Convert.ToHexString(deviceWithKey.GetCertHash(HashAlgorithmName.SHA256)), verifier.Verify(Body, Sign(Body, deviceWithKey)));

        using var rogue = SelfSigned("CN=rogue");
        Assert.Throws<AppleSignatureException>(() => verifier.Verify(Body, Sign(Body, rogue)));
    }

    // ------------------------------------------------------------ compliance

    private static MobileDevice Device(ManagementScenario? scenario = null, Action<MobileDevice>? configure = null)
    {
        var s = scenario ?? new ManagementScenario(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned);
        var device = MobileDevice.CreatePending(Guid.NewGuid(), s, "d1", null, null);
        device.CompleteEnrollment(Now.AddHours(-1));
        device.ApplyInventory(new DeviceInventory
        {
            OsVersion = "14", HasPasscode = true, IsEncrypted = true, Integrity = IntegrityState.Intact,
            Apps = [new InstalledApp("com.mail", "Mail", "1", true)], LastSeenAtUtc = Now.AddMinutes(-5),
        }, Now.AddMinutes(-5));
        configure?.Invoke(device);
        return device;
    }

    private static MobilePolicy Policy(ComplianceRequirements rules, ManagementScenario? scenario = null) =>
        MobilePolicy.Create(Guid.NewGuid(), "P", null, scenario ?? new ManagementScenario(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned),
            new MobilePolicySettings { Compliance = rules });

    private static ComplianceResult Evaluate(MobileDevice device, MobilePolicy? policy, params PolicyAppSpec[] apps) =>
        ComplianceEvaluator.Evaluate(device, policy, apps, Now);

    [Fact]
    public void Healthy_device_meeting_every_rule_is_compliant()
    {
        var result = Evaluate(Device(), Policy(new ComplianceRequirements
        {
            MinimumOsVersion = "12", RequirePasscode = true, RequireEncryption = true, BlockCompromisedDevices = true, MaxHoursSinceCheckIn = 24,
        }));
        Assert.Equal(ComplianceState.Compliant, result.State);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Each_rule_produces_its_own_violation()
    {
        var device = Device(configure: d => d.ApplyInventory(new DeviceInventory
        {
            OsVersion = "10.2", HasPasscode = false, IsEncrypted = false, Integrity = IntegrityState.Compromised, LastSeenAtUtc = Now.AddDays(-3),
        }, Now.AddDays(-3)));
        var result = Evaluate(device, Policy(new ComplianceRequirements
        {
            MinimumOsVersion = "12.1", RequirePasscode = true, RequireEncryption = true, BlockCompromisedDevices = true, MaxHoursSinceCheckIn = 24, RequireAssignedPolicyApplied = true,
        }));
        Assert.Equal(ComplianceState.NonCompliant, result.State);
        Assert.Equal(
            new[] { "compromised_device", "encryption_required", "minimum_os_version", "passcode_required", "policy_not_applied", "stale_check_in" },
            result.Violations.Select(v => v.Rule).Order().ToArray());
    }

    [Theory]
    [InlineData("14", "14", true)]
    [InlineData("14.0.1", "14", true)]
    [InlineData("13.9", "14", false)]
    [InlineData("17.4.1", "17.4", true)]
    [InlineData("17.3", "17.4", false)]
    [InlineData("9", "10", false)]
    public void Os_versions_compare_numerically_not_lexically(string actual, string required, bool meets) =>
        Assert.Equal(meets, ComplianceEvaluator.TryCompare(actual, required) >= 0);

    [Fact]
    public void Unparseable_versions_do_not_count_as_violations()
    {
        Assert.Null(ComplianceEvaluator.TryCompare("beta", "14"));
        var device = Device(configure: d => d.ApplyInventory(new DeviceInventory { OsVersion = "Baklava" }, Now.AddMinutes(-1)));
        Assert.Equal(ComplianceState.Compliant, Evaluate(device, Policy(new ComplianceRequirements { MinimumOsVersion = "14" })).State);
    }

    [Fact]
    public void Missing_signals_are_notes_not_violations_so_platform_limits_do_not_fail_the_fleet()
    {
        var ios = new ManagementScenario(MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned);
        var device = MobileDevice.CreatePending(Guid.NewGuid(), ios, "h", null, null);
        device.CompleteEnrollment(Now);
        device.ApplyInventory(new DeviceInventory { OsVersion = "17.4" }, Now); // no passcode / encryption / integrity signal
        var result = Evaluate(device, Policy(new ComplianceRequirements { RequirePasscode = true, RequireEncryption = true, BlockCompromisedDevices = true }, ios));
        Assert.Equal(ComplianceState.Compliant, result.State);
        Assert.Equal(3, result.Notes.Count);
    }

    [Fact]
    public void Devices_that_never_reported_or_are_not_managed_any_more_are_unknown()
    {
        var policy = Policy(new ComplianceRequirements { RequirePasscode = true });
        var pending = MobileDevice.CreatePending(Guid.NewGuid(), new ManagementScenario(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned), "p", null, null);
        Assert.Equal(ComplianceState.Unknown, Evaluate(pending, policy).State);

        var silent = MobileDevice.CreatePending(Guid.NewGuid(), new ManagementScenario(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned), "s", null, null);
        silent.CompleteEnrollment(Now.AddHours(-1));
        silent.CompleteRemoval(Now, false);
        Assert.Equal(ComplianceState.Unknown, Evaluate(silent, policy).State);
        Assert.Equal(ComplianceState.Unknown, Evaluate(Device(), null).State);
    }

    [Fact]
    public void Required_and_blocked_apps_are_checked_only_once_an_inventory_exists()
    {
        var policy = Policy(new ComplianceRequirements());
        var required = new PolicyAppSpec("com.vpn", "VPN", AppInstallType.ForceInstalled, null, null);
        var blocked = new PolicyAppSpec("com.mail", "Mail", AppInstallType.Blocked, null, null);

        var result = Evaluate(Device(), policy, required, blocked);
        Assert.Equal(new[] { "blocked_app_installed", "required_app_missing" }, result.Violations.Select(v => v.Rule).Order().ToArray());

        var noInventory = MobileDevice.CreatePending(Guid.NewGuid(), new ManagementScenario(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned), "n", null, null);
        noInventory.CompleteEnrollment(Now);
        noInventory.ApplyInventory(new DeviceInventory { OsVersion = "14" }, Now);
        Assert.Equal(ComplianceState.Compliant, Evaluate(noInventory, policy, required).State);
    }

    [Fact]
    public void Policy_application_is_judged_against_the_published_version()
    {
        var policy = Policy(new ComplianceRequirements { RequireAssignedPolicyApplied = true });
        policy.MarkPublished(Now);
        var device = Device();
        Assert.Contains(Evaluate(device, policy).Violations, v => v.Rule == "policy_not_applied");
        device.MarkPolicyApplied(policy.PublishedVersion);
        Assert.Equal(ComplianceState.Compliant, Evaluate(device, policy).State);
        policy.Update("P", null, policy.Settings);
        policy.MarkPublished(Now);
        Assert.Contains(Evaluate(device, policy).Violations, v => v.Rule == "policy_not_applied");
    }
}
