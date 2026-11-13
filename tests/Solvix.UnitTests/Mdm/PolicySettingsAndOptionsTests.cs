namespace Solvix.UnitTests.Mdm;

using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Solvix.Modules.Mdm;
using Solvix.Modules.Mdm.Application;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Security;
using Solvix.SharedKernel.Exceptions;

public sealed class PolicySettingsAndOptionsTests
{
    private static readonly ManagementScenario Android = new(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned);
    private static readonly ManagementScenario Ios = new(MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned);

    private static AesGcmSecretProtector Protector()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Testing");
        return new AesGcmSecretProtector(Options.Create(new MdmOptions()), env);
    }

    private static ValidationException Invalid(ManagementScenario scenario, MobilePolicySettings settings) =>
        Assert.Throws<ValidationException>(() => PolicySettingsSupport.Validate(scenario, settings));

    [Fact]
    public void Out_of_range_values_are_reported_per_field()
    {
        var ex = Invalid(Android, new MobilePolicySettings
        {
            Password = new PasswordSettings { MinLength = 2, MaxFailedAttempts = 1, MaxInactivitySeconds = 5, ExpirationDays = 0, HistoryLength = 99 },
            Compliance = new ComplianceRequirements { MinimumOsVersion = "banana", MaxHoursSinceCheckIn = 0 },
            WifiNetworks = [new WifiNetworkSettings { Ssid = new string('x', 40), Security = WifiSecurity.WpaPersonal, Passphrase = "short" }],
        });
        foreach (var key in new[]
        {
            "settings.password.minLength", "settings.password.maxFailedAttempts", "settings.password.maxInactivitySeconds",
            "settings.password.expirationDays", "settings.password.historyLength", "settings.compliance.minimumOsVersion",
            "settings.compliance.maxHoursSinceCheckIn", "settings.wifiNetworks[0].ssid", "settings.wifiNetworks[0].passphrase",
        })
        {
            Assert.True(ex.Errors.ContainsKey(key), $"missing error for {key}");
        }
    }

    [Fact]
    public void Platform_specific_vpn_and_kiosk_rules()
    {
        Assert.True(Invalid(Android, new MobilePolicySettings { Vpn = [new VpnSettings { Name = "v", Type = VpnType.IkeV2, Server = "s" }] }).Errors.ContainsKey("settings.vpn[0].type"));
        Assert.True(Invalid(Ios, new MobilePolicySettings { Vpn = [new VpnSettings { Name = "v", Type = VpnType.AlwaysOnApp, PackageName = "p" }] }).Errors.ContainsKey("settings.vpn[0].type"));
        Assert.True(Invalid(Android, new MobilePolicySettings { Vpn = [new VpnSettings { Name = "v", Type = VpnType.AlwaysOnApp }] }).Errors.ContainsKey("settings.vpn[0].packageName"));
        Assert.True(Invalid(Android, new MobilePolicySettings { Kiosk = new KioskSettings { AllowedPackages = ["a"] } }).Errors.ContainsKey("settings.kiosk"));
        var dedicated = new ManagementScenario(MobilePlatform.Android, ManagementMode.Dedicated, DeviceOwnership.CompanyOwned);
        Assert.True(Invalid(dedicated, new MobilePolicySettings { Kiosk = new KioskSettings() }).Errors.ContainsKey("settings.kiosk.allowedPackages"));
        PolicySettingsSupport.Validate(dedicated, new MobilePolicySettings { Kiosk = new KioskSettings { AllowedPackages = ["a"] } });
    }

    [Fact]
    public void Enterprise_wifi_needs_a_real_ca_certificate()
    {
        Assert.True(Invalid(Ios, new MobilePolicySettings { WifiNetworks = [new WifiNetworkSettings { Ssid = "c", Security = WifiSecurity.WpaEnterprisePeap, ServerCaCertificatePem = "junk" }] })
            .Errors.ContainsKey("settings.wifiNetworks[0].serverCaCertificatePem"));
    }

    [Fact]
    public void Secrets_are_encrypted_on_the_way_in_kept_on_edit_and_redacted_on_the_way_out()
    {
        var protector = Protector();
        var first = PolicySettingsSupport.Protect(new MobilePolicySettings
        {
            WifiNetworks = [new WifiNetworkSettings { Ssid = "Corp", Passphrase = "super-secret-1" }],
            Vpn = [new VpnSettings { Name = "V", Type = VpnType.IkeV2, Server = "s", SharedSecret = "vpn-secret" }],
        }, null, protector);
        PolicySettingsSupport.EnsureSecrets(first);
        Assert.StartsWith("enc:v1:", first.WifiNetworks[0].Passphrase, StringComparison.Ordinal);
        Assert.StartsWith("enc:v1:", first.Vpn[0].SharedSecret, StringComparison.Ordinal);
        Assert.Equal("super-secret-1", protector.Unprotect(first.WifiNetworks[0].Passphrase));

        // Editing without re-sending the secret keeps the stored one; a client claiming "set" cannot invent one.
        var edited = PolicySettingsSupport.Protect(new MobilePolicySettings
        {
            WifiNetworks = [new WifiNetworkSettings { Ssid = "Corp", AutoJoin = false, PassphraseSet = true }],
            Vpn = [new VpnSettings { Name = "V", Type = VpnType.IkeV2, Server = "s2" }],
        }, first, protector);
        Assert.Equal(first.WifiNetworks[0].Passphrase, edited.WifiNetworks[0].Passphrase);
        Assert.Equal(first.Vpn[0].SharedSecret, edited.Vpn[0].SharedSecret);
        var forged = PolicySettingsSupport.Protect(new MobilePolicySettings { WifiNetworks = [new WifiNetworkSettings { Ssid = "New", PassphraseSet = true }] }, first, protector);
        Assert.Throws<ValidationException>(() => PolicySettingsSupport.EnsureSecrets(forged));

        var redacted = PolicySettingsSupport.Redact(first);
        Assert.Null(redacted.WifiNetworks[0].Passphrase);
        Assert.True(redacted.WifiNetworks[0].PassphraseSet);
        Assert.Null(redacted.Vpn[0].SharedSecret);
        Assert.True(redacted.Vpn[0].SharedSecretSet);

        // A secret supplied for a network type that does not use one is dropped.
        var open = PolicySettingsSupport.Protect(new MobilePolicySettings { WifiNetworks = [new WifiNetworkSettings { Ssid = "Open", Security = WifiSecurity.Open, Passphrase = "ignored" }] }, null, protector);
        Assert.Null(open.WifiNetworks[0].Passphrase);
    }

    [Fact]
    public void Managed_configuration_must_be_a_flat_json_object()
    {
        Assert.Equal("{\"a\":1,\"b\":\"x\",\"c\":true}", PolicySettingsSupport.NormalizeManagedConfiguration("{ \"a\": 1, \"b\": \"x\", \"c\": true }", "p"));
        Assert.Null(PolicySettingsSupport.NormalizeManagedConfiguration("  ", "p"));
        Assert.Throws<ValidationException>(() => PolicySettingsSupport.NormalizeManagedConfiguration("[1]", "p"));
        Assert.Throws<ValidationException>(() => PolicySettingsSupport.NormalizeManagedConfiguration("{\"a\":{\"b\":1}}", "p"));
        Assert.Throws<ValidationException>(() => PolicySettingsSupport.NormalizeManagedConfiguration("{\"a\":null}", "p"));
        Assert.Throws<ValidationException>(() => PolicySettingsSupport.NormalizeManagedConfiguration("{broken", "p"));
    }

    // -------------------------------------------------------------- startup options

    private static ValidateOptionsResult Validate(MdmOptions options, string environment)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        return new MdmOptionsValidator(env).Validate(null, options);
    }

    [Fact]
    public void Production_configuration_fails_fast_when_unsafe()
    {
        Assert.True(Validate(new MdmOptions { PublicBaseUrl = "https://mdm.example.test" }, "Production").Failed); // no key
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.False(Validate(new MdmOptions { SecretEncryptionKey = key, PublicBaseUrl = "https://mdm.example.test" }, "Production").Failed);
        Assert.True(Validate(new MdmOptions { SecretEncryptionKey = "AAAA", PublicBaseUrl = "https://mdm.example.test" }, "Production").Failed);
        Assert.True(Validate(new MdmOptions { SecretEncryptionKey = "not base64!", PublicBaseUrl = "https://x" }, "Development").Failed);

        var http = new MdmOptions { SecretEncryptionKey = key, PublicBaseUrl = "http://mdm.example.test", Apple = new AppleMdmOptions { PushTopic = "t", IdentityCaCertificatePem = "pem" } };
        Assert.True(Validate(http, "Production").Failed);

        var untrusted = new MdmOptions { SecretEncryptionKey = key, PublicBaseUrl = "https://mdm.example.test", Apple = new AppleMdmOptions { AllowUntrustedDeviceIdentity = true } };
        Assert.True(Validate(untrusted, "Production").Failed);
        Assert.False(Validate(untrusted, "Development").Failed);

        var appleNoCa = new MdmOptions { SecretEncryptionKey = key, PublicBaseUrl = "https://mdm.example.test", Apple = new AppleMdmOptions { PushTopic = "t" } };
        Assert.True(Validate(appleNoCa, "Production").Failed);
        Assert.False(Validate(new MdmOptions(), "Development").Failed);
    }
}
