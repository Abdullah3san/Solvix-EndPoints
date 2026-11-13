namespace Solvix.UnitTests.Mdm;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers;
using Solvix.Modules.Mdm.Providers.Apple;

public sealed class ApplePlistAndProfileTests
{
    private static readonly ManagementScenario Supervised = new(MobilePlatform.Ios, ManagementMode.AutomatedDeviceEnrollment, DeviceOwnership.CompanyOwned);
    private static readonly ManagementScenario Unsupervised = new(MobilePlatform.Ios, ManagementMode.DeviceEnrollment, DeviceOwnership.CompanyOwned);
    private static readonly ManagementScenario UserEnrollment = new(MobilePlatform.Ios, ManagementMode.UserEnrollment, DeviceOwnership.PersonallyOwned);

    private static AppleProfileTranslation Build(ManagementScenario scenario, MobilePolicySettings settings, params PolicyAppSpec[] apps) =>
        AppleProfileBuilder.BuildPolicyProfile(Guid.NewGuid(), "Policy", 3, scenario, settings, apps, s => s is null ? null : "plain:" + s);

    private static List<Dictionary<string, object?>> Payloads(AppleProfileTranslation t) =>
        ((List<object?>)ApplePlist.ParseDictionary(t.Profile)["PayloadContent"]!).OfType<Dictionary<string, object?>>().ToList();

    private static Dictionary<string, object?> Payload(AppleProfileTranslation t, string type) =>
        Payloads(t).Single(p => (string?)p["PayloadType"] == type);

    // ------------------------------------------------------------------ plist

    [Fact]
    public void Plist_round_trips_every_supported_type()
    {
        var when = new DateTimeOffset(2026, 11, 13, 10, 30, 5, TimeSpan.Zero);
        var original = new Dictionary<string, object?>
        {
            ["str"] = "héllo <&> \"quotes\"",
            ["int"] = 42L,
            ["neg"] = -7L,
            ["real"] = 1.5,
            ["yes"] = true,
            ["no"] = false,
            ["date"] = when,
            ["data"] = new byte[] { 0, 1, 2, 250 },
            ["list"] = new List<object?> { "a", 1L, new Dictionary<string, object?> { ["k"] = "v" } },
            ["nested"] = new Dictionary<string, object?> { ["deep"] = new List<object?>() },
        };
        var parsed = ApplePlist.ParseDictionary(ApplePlist.Serialize(original));
        Assert.Equal("héllo <&> \"quotes\"", parsed["str"]);
        Assert.Equal(42L, parsed["int"]);
        Assert.Equal(-7L, parsed["neg"]);
        Assert.Equal(1.5, parsed["real"]);
        Assert.Equal(true, parsed["yes"]);
        Assert.Equal(false, parsed["no"]);
        Assert.Equal(when, parsed["date"]);
        Assert.Equal(new byte[] { 0, 1, 2, 250 }, parsed["data"]);
        Assert.Equal(3, ((List<object?>)parsed["list"]!).Count);
        Assert.Empty((List<object?>)((Dictionary<string, object?>)parsed["nested"]!)["deep"]!);
    }

    [Fact]
    public void Plist_parser_does_not_resolve_external_entities_and_rejects_malformed_input()
    {
        var xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE plist [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <plist version="1.0"><dict><key>a</key><string>&xxe;</string></dict></plist>
            """u8.ToArray();
        // With DTD processing ignored, the undefined entity is an error rather than a file read.
        Assert.Throws<FormatException>(() => ApplePlist.ParseDictionary(xxe));
        Assert.Throws<FormatException>(() => ApplePlist.ParseDictionary([]));
        Assert.Throws<FormatException>(() => ApplePlist.ParseDictionary("not xml"u8));
        Assert.Throws<FormatException>(() => ApplePlist.ParseDictionary("<plist><array/></plist>"u8)); // root must be a dictionary
        Assert.Throws<FormatException>(() => ApplePlist.ParseDictionary("<plist><dict><key>a</key></dict></plist>"u8));
        Assert.Throws<FormatException>(() => ApplePlist.ParseDictionary("<plist><dict><key>a</key><weird/></dict></plist>"u8));
        var deep = string.Concat(Enumerable.Repeat("<array>", 50)) + string.Concat(Enumerable.Repeat("</array>", 50));
        Assert.Throws<FormatException>(() => ApplePlist.Parse(System.Text.Encoding.UTF8.GetBytes($"<plist>{deep}</plist>")));
    }

    [Fact]
    public void Apple_doctype_is_accepted()
    {
        var real = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>MessageType</key><string>Authenticate</string></dict></plist>
            """u8.ToArray();
        Assert.Equal("Authenticate", ApplePlist.ParseDictionary(real)["MessageType"]);
    }

    // --------------------------------------------------------------- commands

    [Fact]
    public void Command_plists_carry_the_request_type_and_arguments()
    {
        var id = Guid.NewGuid();
        var lockCommand = ApplePlist.ParseDictionary(AppleCommandBuilder.DeviceLock(id, "Call IT"));
        Assert.Equal(id.ToString().ToUpperInvariant(), lockCommand["CommandUUID"]);
        var body = (Dictionary<string, object?>)lockCommand["Command"]!;
        Assert.Equal("DeviceLock", body["RequestType"]);
        Assert.Equal("Call IT", body["Message"]);
        Assert.False(((Dictionary<string, object?>)ApplePlist.ParseDictionary(AppleCommandBuilder.DeviceLock(id, null))["Command"]!).ContainsKey("Message"));

        var install = (Dictionary<string, object?>)ApplePlist.ParseDictionary(AppleCommandBuilder.InstallApplication(id, 123456789, new Dictionary<string, object?> { ["k"] = "v" }, true))["Command"]!;
        Assert.Equal(123456789L, install["iTunesStoreID"]);
        Assert.Equal("Managed", install["ChangeManagementState"]);
        Assert.Equal("v", ((Dictionary<string, object?>)install["Configuration"]!)["k"]);
        Assert.Equal("EraseDevice", ((Dictionary<string, object?>)ApplePlist.ParseDictionary(AppleCommandBuilder.EraseDevice(id))["Command"]!)["RequestType"]);
        Assert.Equal(new byte[] { 1, 2 }, ((Dictionary<string, object?>)ApplePlist.ParseDictionary(AppleCommandBuilder.ClearPasscode(id, [1, 2]))["Command"]!)["UnlockToken"]);
        Assert.Equal("a.b", ((Dictionary<string, object?>)ApplePlist.ParseDictionary(AppleCommandBuilder.RemoveProfile(id, "a.b"))["Command"]!)["Identifier"]);
        var queries = (List<object?>)((Dictionary<string, object?>)ApplePlist.ParseDictionary(AppleCommandBuilder.DeviceInformation(id))["Command"]!)["Queries"]!;
        Assert.Contains("OSVersion", queries);
        Assert.Contains("PasscodePresent", queries);
    }

    // ------------------------------------------------------------- enrollment

    private static AppleEnrollmentProfileOptions Options(ManagementScenario scenario) =>
        new("handle123", scenario, "https://mdm.example.test/", "com.apple.mgmt.External.x", "https://scep.example.test/", "challenge", "Acme");

    [Fact]
    public void Device_enrollment_profile_requests_signed_messages_and_full_access_rights()
    {
        var profile = ApplePlist.ParseDictionary(AppleProfileBuilder.BuildEnrollmentProfile(Options(Unsupervised)));
        var payloads = ((List<object?>)profile["PayloadContent"]!).OfType<Dictionary<string, object?>>().ToList();
        var mdm = payloads.Single(p => (string?)p["PayloadType"] == "com.apple.mdm");
        var scep = payloads.Single(p => (string?)p["PayloadType"] == "com.apple.security.scep");
        Assert.Equal(scep["PayloadUUID"], mdm["IdentityCertificateUUID"]);
        Assert.Equal("https://mdm.example.test/api/v1/mdm/apple/handle123/checkin", mdm["CheckInURL"]);
        Assert.Equal("https://mdm.example.test/api/v1/mdm/apple/handle123/server", mdm["ServerURL"]);
        Assert.Equal(true, mdm["SignMessage"]);
        Assert.Equal(true, mdm["CheckOutWhenRemoved"]);
        Assert.Equal(8191L, mdm["AccessRights"]);
        Assert.False(mdm.ContainsKey("EnrollmentMode"));
        Assert.Equal("challenge", ((Dictionary<string, object?>)scep["PayloadContent"]!)["Challenge"]);
        Assert.Equal("com.solvix.mdm.enrollment.handle123", profile["PayloadIdentifier"]);
    }

    [Fact]
    public void User_enrollment_profile_asks_for_byod_mode_and_no_device_access_rights()
    {
        var profile = ApplePlist.ParseDictionary(AppleProfileBuilder.BuildEnrollmentProfile(Options(UserEnrollment)));
        var mdm = ((List<object?>)profile["PayloadContent"]!).OfType<Dictionary<string, object?>>().Single(p => (string?)p["PayloadType"] == "com.apple.mdm");
        Assert.Equal("BYOD", mdm["EnrollmentMode"]);
        Assert.False(mdm.ContainsKey("AccessRights"));
        Assert.Contains("personal data stays private", (string)profile["PayloadDescription"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => AppleProfileBuilder.BuildEnrollmentProfile(Options(new ManagementScenario(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned))));
    }

    // ----------------------------------------------------------------- policy

    [Fact]
    public void Policy_profile_has_a_constant_identifier_so_reassignment_replaces_the_previous_policy()
    {
        var a = Build(Unsupervised, new MobilePolicySettings());
        var b = Build(Unsupervised, new MobilePolicySettings());
        Assert.Equal(AppleProfileBuilder.PolicyProfileIdentifier, a.Identifier);
        Assert.Equal(a.Identifier, b.Identifier);
        var rootA = ApplePlist.ParseDictionary(a.Profile);
        var rootB = ApplePlist.ParseDictionary(b.Profile);
        Assert.NotEqual(rootA["PayloadUUID"], rootB["PayloadUUID"]); // different policies are different profiles of the same identifier
    }

    [Fact]
    public void Policy_profile_uuids_are_deterministic_for_the_same_policy_version()
    {
        var id = Guid.NewGuid();
        PolicyAppSpec[] none = [];
        var one = AppleProfileBuilder.BuildPolicyProfile(id, "P", 2, Unsupervised, new MobilePolicySettings(), none, s => s);
        var two = AppleProfileBuilder.BuildPolicyProfile(id, "P", 2, Unsupervised, new MobilePolicySettings(), none, s => s);
        Assert.Equal(ApplePlist.ParseDictionary(one.Profile)["PayloadUUID"], ApplePlist.ParseDictionary(two.Profile)["PayloadUUID"]);
        var three = AppleProfileBuilder.BuildPolicyProfile(id, "P", 3, Unsupervised, new MobilePolicySettings(), none, s => s);
        Assert.NotEqual(ApplePlist.ParseDictionary(one.Profile)["PayloadUUID"], ApplePlist.ParseDictionary(three.Profile)["PayloadUUID"]);
    }

    [Fact]
    public void Passcode_payload_maps_quality_and_limits()
    {
        var t = Build(Supervised, new MobilePolicySettings
        {
            Password = new PasswordSettings { Quality = PasswordQuality.Complex, MinLength = 8, MaxFailedAttempts = 99, MaxInactivitySeconds = 100, ExpirationDays = 60, HistoryLength = 3 },
        });
        var p = Payload(t, "com.apple.mobiledevice.passwordpolicy");
        Assert.Equal(true, p["forcePIN"]);
        Assert.Equal(8L, p["minLength"]);
        Assert.Equal(true, p["requireAlphanumeric"]);
        Assert.Equal(false, p["allowSimple"]);
        Assert.Equal(1L, p["minComplexChars"]);
        Assert.Equal(11L, p["maxFailedAttempts"]); // clamped to what iOS accepts
        Assert.Equal(2L, p["maxInactivity"]);       // minutes, rounded up
        Assert.Equal(60L, p["maxPINAgeInDays"]);
        Assert.Equal(3L, p["pinHistory"]);
    }

    [Fact]
    public void Supervised_devices_get_supervised_only_restrictions_while_unsupervised_ones_report_them_skipped()
    {
        var settings = new MobilePolicySettings
        {
            Restrictions = new RestrictionSettings { DisableCamera = true, DisableFactoryReset = true },
            AppControl = AppControlMode.ApprovedAppsOnly,
        };
        var blocked = new PolicyAppSpec("com.game", "Game", AppInstallType.Blocked, null, null);
        var allowed = new PolicyAppSpec("com.mail", "Mail", AppInstallType.ForceInstalled, 1, null);

        var supervised = Build(Supervised, settings, blocked, allowed);
        var r = Payload(supervised, "com.apple.applicationaccess");
        Assert.Equal(false, r["allowCamera"]);
        Assert.Equal(false, r["allowEraseContentAndSettings"]);
        Assert.Contains("com.game", ((List<object?>)r["blacklistedAppBundleIDs"]!).Cast<string>());
        Assert.Equal(new[] { "com.mail" }, ((List<object?>)r["whitelistedAppBundleIDs"]!).Cast<string>());
        Assert.Empty(supervised.Skipped);

        var plain = Build(Unsupervised, settings, blocked, allowed);
        var pr = Payload(plain, "com.apple.applicationaccess");
        Assert.Equal(false, pr["allowCamera"]);
        Assert.False(pr.ContainsKey("allowEraseContentAndSettings"));
        Assert.False(pr.ContainsKey("blacklistedAppBundleIDs"));
        Assert.Equal(3, plain.Skipped.Count);
    }

    [Fact]
    public void User_enrollment_drops_device_wide_settings_and_always_blocks_managed_to_personal_sharing()
    {
        var t = Build(UserEnrollment, new MobilePolicySettings
        {
            Password = new PasswordSettings { MaxFailedAttempts = 5, MinLength = 6 },
            Restrictions = new RestrictionSettings { DisableCamera = true, DisableScreenCapture = true, DisableFactoryReset = true },
            Vpn = [new VpnSettings { Name = "v", Type = VpnType.IkeV2, Server = "vpn.example.test", SharedSecret = "s" }],
            WifiNetworks = [new WifiNetworkSettings { Ssid = "Guest", Security = WifiSecurity.Open }],
        });
        var r = Payload(t, "com.apple.applicationaccess");
        Assert.Equal(false, r["allowOpenFromManagedToUnmanaged"]);
        Assert.Equal(false, r["allowManagedToWriteUnmanagedContacts"]);
        Assert.False(r.ContainsKey("allowCamera"));
        Assert.False(r.ContainsKey("allowScreenShot"));
        Assert.False(r.ContainsKey("allowEraseContentAndSettings"));
        Assert.DoesNotContain(Payloads(t), p => (string?)p["PayloadType"] == "com.apple.vpn.managed");
        Assert.False(Payload(t, "com.apple.mobiledevice.passwordpolicy").ContainsKey("maxFailedAttempts"));
        Assert.NotNull(Payload(t, "com.apple.wifi.managed"));
        Assert.Contains(t.Skipped, s => s.StartsWith("maxFailedAttempts", StringComparison.Ordinal));
        Assert.Contains(t.Skipped, s => s.StartsWith("vpn v", StringComparison.Ordinal));
        // camera, screen capture, factory reset, wipe-on-failed-attempts and VPN were all dropped, each citing User Enrollment.
        Assert.Equal(5, t.Skipped.Count);
        Assert.All(t.Skipped, skipped => Assert.Contains("User Enrollment", skipped, StringComparison.Ordinal));
    }

    [Fact]
    public void Wifi_enterprise_adds_a_trusted_root_and_open_or_personal_networks_work()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Radius CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var t = Build(Unsupervised, new MobilePolicySettings
        {
            WifiNetworks =
            [
                new WifiNetworkSettings { Ssid = "Home", Security = WifiSecurity.WpaPersonal, Passphrase = "pw" },
                new WifiNetworkSettings { Ssid = "Corp", Security = WifiSecurity.WpaEnterprisePeap, ServerCaCertificatePem = ca.ExportCertificatePem(), ServerNames = ["radius.example.test"] },
                new WifiNetworkSettings { Ssid = "Bad", Security = WifiSecurity.WpaEnterprisePeap, ServerCaCertificatePem = "nope" },
            ],
        });
        var wifi = Payloads(t).Where(p => (string?)p["PayloadType"] == "com.apple.wifi.managed").ToList();
        Assert.Equal(2, wifi.Count);
        Assert.Equal("plain:pw", wifi.Single(p => (string?)p["SSID_STR"] == "Home")["Password"]);
        var corp = wifi.Single(p => (string?)p["SSID_STR"] == "Corp");
        var root = Payload(t, "com.apple.security.root");
        Assert.Equal(ca.RawData, root["PayloadContent"]);
        Assert.Equal(root["PayloadUUID"], ((List<object?>)corp["PayloadCertificateAnchorUUID"]!)[0]);
        Assert.Equal(false, ((Dictionary<string, object?>)corp["EAPClientConfiguration"]!)["TLSAllowTrustExceptions"]);
        Assert.Contains(t.Skipped, s => s.StartsWith("wifi Bad", StringComparison.Ordinal));
        Assert.Equal(Payloads(t).Select(p => p["PayloadUUID"]).Count(), Payloads(t).Select(p => p["PayloadUUID"]).Distinct().Count());
    }

    [Fact]
    public void Ikev2_vpn_carries_the_revealed_secret_and_kiosk_is_reported_as_unsupported()
    {
        var t = Build(Unsupervised, new MobilePolicySettings
        {
            Vpn = [new VpnSettings { Name = "Corp", Type = VpnType.IkeV2, Server = "vpn.example.test", RemoteIdentifier = "vpn.example.test", SharedSecret = "enc" }],
            Kiosk = new KioskSettings { AllowedPackages = ["x"] },
        });
        var vpn = Payload(t, "com.apple.vpn.managed");
        var ike = (Dictionary<string, object?>)vpn["IKEv2"]!;
        Assert.Equal("IKEv2", vpn["VPNType"]);
        Assert.Equal("vpn.example.test", ike["RemoteAddress"]);
        Assert.Equal("plain:enc"u8.ToArray(), ike["SharedSecret"]);
        Assert.Contains(t.Skipped, s => s.StartsWith("kiosk", StringComparison.Ordinal));
    }

    [Fact]
    public void Translation_is_rejected_for_the_wrong_platform()
    {
        Assert.Throws<ArgumentException>(() => Build(new ManagementScenario(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned), new MobilePolicySettings()));
    }
}
