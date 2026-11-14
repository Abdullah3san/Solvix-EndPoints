namespace Solvix.UnitTests.Mdm;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers;
using Solvix.Modules.Mdm.Providers.Android;

public sealed class AndroidPolicyTranslatorTests
{
    private static readonly ManagementScenario FullyManaged = new(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned);
    private static readonly ManagementScenario Dedicated = new(MobilePlatform.Android, ManagementMode.Dedicated, DeviceOwnership.CompanyOwned);
    private static readonly ManagementScenario CompanyProfile = new(MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.CompanyOwned);
    private static readonly ManagementScenario Byod = new(MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.PersonallyOwned);

    private static AndroidPolicyTranslation Translate(ManagementScenario scenario, MobilePolicySettings settings, params PolicyAppSpec[] apps) =>
        AndroidPolicyTranslator.Translate(scenario, settings, apps, s => s is null ? null : "plain:" + s);

    private static string CaPem()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test RADIUS CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return cert.ExportCertificatePem();
    }

    [Fact]
    public void Policy_names_are_stable_per_enterprise_and_policy()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"enterprises/LC1/policies/solvix-{id:N}", AndroidPolicyTranslator.PolicyName("enterprises/LC1", id));
    }

    [Fact]
    public void Password_policy_uses_device_scope_when_fully_managed_and_profile_scope_for_work_profiles()
    {
        var settings = new MobilePolicySettings
        {
            Password = new PasswordSettings { Quality = PasswordQuality.Complex, MinLength = 10, MaxFailedAttempts = 5, MaxInactivitySeconds = 90, ExpirationDays = 30, HistoryLength = 4 },
        };
        var device = Translate(FullyManaged, settings).Policy;
        var rule = device["passwordPolicies"]![0]!;
        Assert.Equal("COMPLEX", (string?)rule["passwordQuality"]);
        Assert.Equal(10, (int?)rule["passwordMinimumLength"]);
        Assert.Equal(5, (int?)rule["maximumFailedPasswordsForWipe"]);
        Assert.Equal("2592000s", (string?)rule["passwordExpirationTimeout"]);
        Assert.Equal(4, (int?)rule["passwordHistoryLength"]);
        Assert.Equal("SCOPE_DEVICE", (string?)rule["passwordScope"]);
        Assert.Equal("90000", (string?)device["maximumTimeToLock"]);
        Assert.Equal("SCOPE_PROFILE", (string?)Translate(Byod, settings).Policy["passwordPolicies"]![0]!["passwordScope"]);
    }

    [Fact]
    public void Reporting_is_limited_to_what_compliance_needs()
    {
        var reporting = Translate(Byod, new MobilePolicySettings()).Policy["statusReportingSettings"]!;
        Assert.True((bool?)reporting["softwareInfoEnabled"]);
        Assert.False((bool?)reporting["networkInfoEnabled"]);
        Assert.False((bool?)reporting["hardwareStatusEnabled"]);
        Assert.False((bool?)reporting["memoryInfoEnabled"]);
    }

    [Fact]
    public void Byod_boundary_is_always_enforced_and_device_level_settings_are_skipped_with_a_reason()
    {
        var result = Translate(Byod, new MobilePolicySettings
        {
            Restrictions = new RestrictionSettings { DisableCamera = true, DisableUsbFileTransfer = true, DisableFactoryReset = true, DisableUnknownSources = true },
        });
        var cross = result.Policy["crossProfilePolicies"]!;
        Assert.Equal("DATA_SHARING_FROM_WORK_TO_PERSONAL_DISALLOWED", (string?)cross["crossProfileDataSharing"]);
        Assert.Equal("COPY_FROM_WORK_TO_PERSONAL_DISALLOWED", (string?)cross["crossProfileCopyPaste"]);
        Assert.Null(result.Policy["deviceConnectivityManagement"]);
        Assert.Null(result.Policy["factoryResetDisabled"]);
        Assert.True((bool?)result.Policy["cameraDisabled"]);
        Assert.Equal("DISALLOW_INSTALL", (string?)result.Policy["advancedSecurityOverrides"]!["untrustedAppsPolicy"]);
        Assert.Equal(2, result.Skipped.Count);
        Assert.Contains(result.Applied, a => a == "camera disabled (work profile only)");
    }

    [Fact]
    public void Company_owned_fully_managed_devices_get_device_level_restrictions_and_no_cross_profile_policy()
    {
        var result = Translate(FullyManaged, new MobilePolicySettings
        {
            Restrictions = new RestrictionSettings { DisableUsbFileTransfer = true, DisableFactoryReset = true, BlockManagedToPersonalSharing = true },
        });
        Assert.Equal("DISALLOW_USB_FILE_TRANSFER", (string?)result.Policy["deviceConnectivityManagement"]!["usbDataAccess"]);
        Assert.True((bool?)result.Policy["factoryResetDisabled"]);
        Assert.Null(result.Policy["crossProfilePolicies"]);
        Assert.Contains(result.Skipped, s => s.StartsWith("blockManagedToPersonalSharing", StringComparison.Ordinal));
    }

    [Fact]
    public void Company_work_profile_only_gets_the_boundary_when_asked()
    {
        Assert.Null(Translate(CompanyProfile, new MobilePolicySettings()).Policy["crossProfilePolicies"]);
        Assert.NotNull(Translate(CompanyProfile, new MobilePolicySettings { Restrictions = new RestrictionSettings { BlockManagedToPersonalSharing = true } }).Policy["crossProfilePolicies"]);
    }

    [Fact]
    public void Applications_carry_install_type_managed_configuration_and_play_store_mode()
    {
        var result = Translate(
            FullyManaged,
            new MobilePolicySettings { AppControl = AppControlMode.ApprovedAppsOnly },
            new PolicyAppSpec("com.mail", "Mail", AppInstallType.ForceInstalled, null, "{\"host\":\"h\"}"),
            new PolicyAppSpec("com.game", "Game", AppInstallType.Blocked, null, "{\"x\":1}"),
            new PolicyAppSpec("com.opt", "Optional", AppInstallType.Available, null, null));
        var apps = result.Policy["applications"]!.AsArray();
        Assert.Equal("WHITELIST", (string?)result.Policy["playStoreMode"]);
        Assert.Equal("FORCE_INSTALLED", (string?)apps.Single(a => (string?)a!["packageName"] == "com.mail")!["installType"]);
        Assert.Equal("h", (string?)apps.Single(a => (string?)a!["packageName"] == "com.mail")!["managedConfiguration"]!["host"]);
        Assert.Equal("BLOCKED", (string?)apps.Single(a => (string?)a!["packageName"] == "com.game")!["installType"]);
        Assert.Null(apps.Single(a => (string?)a!["packageName"] == "com.game")!["managedConfiguration"]); // blocked apps get no configuration
        Assert.Equal("BLACKLIST", (string?)Translate(FullyManaged, new MobilePolicySettings()).Policy["playStoreMode"]);
    }

    [Fact]
    public void Kiosk_apps_are_only_accepted_on_dedicated_devices()
    {
        var kiosk = new PolicyAppSpec("com.pos", "POS", AppInstallType.Kiosk, null, null);
        var onDedicated = Translate(Dedicated, new MobilePolicySettings { Kiosk = new KioskSettings { AllowedPackages = ["com.pos", "com.scanner"] } }, kiosk);
        Assert.Equal("KIOSK", (string?)onDedicated.Policy["applications"]!.AsArray().Single(a => (string?)a!["packageName"] == "com.pos")!["installType"]);
        Assert.Contains(onDedicated.Policy["applications"]!.AsArray(), a => (string?)a!["packageName"] == "com.scanner"); // allow-list entries are installed
        Assert.True((bool?)onDedicated.Policy["kioskCustomLauncherEnabled"]);
        Assert.NotNull(onDedicated.Policy["kioskCustomization"]);

        var onFullyManaged = Translate(FullyManaged, new MobilePolicySettings { Kiosk = new KioskSettings { AllowedPackages = ["com.pos"] } }, kiosk);
        Assert.Null(onFullyManaged.Policy["kioskCustomization"]);
        Assert.Contains(onFullyManaged.Skipped, s => s.Contains("kiosk", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(onFullyManaged.Policy["applications"]?.AsArray() ?? [], a => (string?)a!["installType"] == "KIOSK");
    }

    [Fact]
    public void Wifi_personal_enterprise_and_open_networks()
    {
        var result = Translate(FullyManaged, new MobilePolicySettings
        {
            WifiNetworks =
            [
                new WifiNetworkSettings { Ssid = "Home", Security = WifiSecurity.WpaPersonal, Passphrase = "pw", Hidden = true, AutoJoin = false },
                new WifiNetworkSettings { Ssid = "Guest", Security = WifiSecurity.Open },
                new WifiNetworkSettings { Ssid = "Corp", Security = WifiSecurity.WpaEnterprisePeap, ServerCaCertificatePem = CaPem(), ServerNames = ["radius.example.test"], AnonymousIdentity = "anon@example.test" },
                new WifiNetworkSettings { Ssid = "Broken", Security = WifiSecurity.WpaEnterprisePeap, ServerCaCertificatePem = "garbage" },
            ],
        });
        var networks = result.Policy["openNetworkConfiguration"]!["NetworkConfigurations"]!.AsArray();
        Assert.Equal(3, networks.Count);
        var home = networks.Single(n => (string?)n!["Name"] == "Home")!["WiFi"]!;
        Assert.Equal("WPA-PSK", (string?)home["Security"]);
        Assert.Equal("plain:pw", (string?)home["Passphrase"]);
        Assert.True((bool?)home["HiddenSSID"]);
        Assert.False((bool?)home["AutoConnect"]);
        Assert.Equal("None", (string?)networks.Single(n => (string?)n!["Name"] == "Guest")!["WiFi"]!["Security"]);
        var corp = networks.Single(n => (string?)n!["Name"] == "Corp")!["WiFi"]!;
        Assert.Equal("WPA-EAP", (string?)corp["Security"]);
        Assert.Equal("PEAP", (string?)corp["EAP"]!["Outer"]);
        Assert.False((bool?)corp["EAP"]!["UseSystemCAs"]);
        var caRef = (string?)corp["EAP"]!["ServerCARefs"]![0];
        Assert.Equal(caRef, (string?)result.Policy["openNetworkConfiguration"]!["Certificates"]![0]!["GUID"]);
        Assert.Contains(result.Skipped, s => s.StartsWith("wifi Broken", StringComparison.Ordinal));
    }

    [Fact]
    public void Always_on_vpn_forces_the_client_app_and_only_one_is_allowed()
    {
        var result = Translate(FullyManaged, new MobilePolicySettings
        {
            Vpn =
            [
                new VpnSettings { Name = "Primary", Type = VpnType.AlwaysOnApp, PackageName = "org.vpn", Lockdown = true },
                new VpnSettings { Name = "Second", Type = VpnType.AlwaysOnApp, PackageName = "org.vpn2" },
                new VpnSettings { Name = "IKE", Type = VpnType.IkeV2, Server = "vpn.example.test" },
            ],
        });
        Assert.Equal("org.vpn", (string?)result.Policy["alwaysOnVpnPackage"]!["packageName"]);
        Assert.True((bool?)result.Policy["alwaysOnVpnPackage"]!["lockdownEnabled"]);
        Assert.Equal("FORCE_INSTALLED", (string?)result.Policy["applications"]!.AsArray().Single(a => (string?)a!["packageName"] == "org.vpn")!["installType"]);
        Assert.Equal(2, result.Skipped.Count);
    }

    [Theory]
    [InlineData("8", 26)]
    [InlineData("12", 31)]
    [InlineData("13.1", 33)]
    [InlineData("14", 34)]
    [InlineData("16", 36)]
    public void Minimum_os_version_maps_to_an_api_level(string release, int apiLevel) =>
        Assert.Equal(apiLevel, (int?)Translate(FullyManaged, new MobilePolicySettings { Compliance = new ComplianceRequirements { MinimumOsVersion = release } }).Policy["minimumApiLevel"]);

    [Fact]
    public void Translation_is_rejected_for_the_wrong_platform()
    {
        Assert.Throws<ArgumentException>(() => Translate(new ManagementScenario(MobilePlatform.Ios, ManagementMode.UserEnrollment, DeviceOwnership.PersonallyOwned), new MobilePolicySettings()));
    }

    [Fact]
    public void Inventory_mapping_reads_posture_and_marks_work_profile_apps_as_managed()
    {
        var info = new AndroidDeviceInfo
        {
            Name = "enterprises/e/devices/d", ManagementMode = "PROFILE_OWNER", Ownership = "PERSONALLY_OWNED", Brand = "google", Model = "Pixel 8",
            AndroidVersion = "14", DevicePosture = "POTENTIALLY_COMPROMISED", IsDeviceSecure = true, IsEncrypted = true, PolicyCompliant = false,
            Applications = [new AndroidAppReport("com.a", "A", "1", null)],
        };
        var inventory = AndroidInventoryMapper.ToInventory(info);
        Assert.Equal(IntegrityState.Compromised, inventory.Integrity);
        Assert.Equal("google Pixel 8", inventory.Model);
        Assert.False(inventory.ProviderReportsCompliant);
        Assert.True(inventory.Apps![0].IsManaged);
        Assert.Equal(new ManagementScenario(MobilePlatform.Android, ManagementMode.WorkProfile, DeviceOwnership.PersonallyOwned), AndroidInventoryMapper.InferScenario(info));
        Assert.Equal(new ManagementScenario(MobilePlatform.Android, ManagementMode.FullyManaged, DeviceOwnership.CompanyOwned), AndroidInventoryMapper.InferScenario(new AndroidDeviceInfo { Name = "x", ManagementMode = "DEVICE_OWNER" }));
        Assert.Equal(IntegrityState.Unknown, AndroidInventoryMapper.ToInventory(new AndroidDeviceInfo { Name = "x" }).Integrity);
    }
}
