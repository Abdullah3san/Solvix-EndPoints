namespace Solvix.Modules.Mdm.Providers.Android;

using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solvix.Modules.Mdm.Domain;

/// <summary>
/// Pure translation from Solvix policy settings to an Android Management API
/// <c>Policy</c> resource, for one management scenario. Settings that the
/// scenario's ownership boundary forbids are not emitted and are listed in the
/// report, so an administrator can see exactly what a BYOD policy did not do.
/// </summary>
public static class AndroidPolicyTranslator
{
    private static readonly Dictionary<int, int> ApiLevelByRelease = new()
    {
        [8] = 26, [9] = 28, [10] = 29, [11] = 30, [12] = 31, [13] = 33, [14] = 34, [15] = 35, [16] = 36,
    };

    public static string PolicyName(string enterpriseName, Guid policyId) =>
        $"{enterpriseName}/policies/solvix-{policyId:N}";

    public static AndroidPolicyTranslation Translate(
        ManagementScenario scenario,
        MobilePolicySettings settings,
        IReadOnlyList<PolicyAppSpec> apps,
        Func<string?, string?> revealSecret)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(revealSecret);
        scenario.EnsureValid();
        if (scenario.Platform != MobilePlatform.Android)
        {
            throw new ArgumentException("Not an Android scenario.", nameof(scenario));
        }

        var applied = new List<string>();
        var skipped = new List<string>();
        var policy = new JsonObject();
        var personal = scenario.IsPersonalBoundary;
        var workProfile = scenario.Mode == ManagementMode.WorkProfile;

        // Privacy-minimal status reporting: only what compliance evaluation needs.
        policy["statusReportingSettings"] = new JsonObject
        {
            ["applicationReportsEnabled"] = true,
            ["deviceSettingsEnabled"] = true,
            ["softwareInfoEnabled"] = true,
            ["hardwareStatusEnabled"] = false,
            ["memoryInfoEnabled"] = false,
            ["networkInfoEnabled"] = false,
            ["displayInfoEnabled"] = false,
            ["powerManagementEventsEnabled"] = false,
        };

        TranslatePassword(policy, scenario, settings.Password, applied);
        TranslateRestrictions(policy, scenario, settings.Restrictions, applied, skipped);
        TranslateCompliance(policy, settings.Compliance, applied);
        TranslateNetworks(policy, settings.WifiNetworks, revealSecret, applied, skipped);
        var vpnPackage = TranslateVpn(policy, settings.Vpn, applied, skipped);
        TranslateKiosk(policy, scenario, settings.Kiosk, skipped, applied);

        var applications = new JsonArray();
        foreach (var app in apps)
        {
            if (app.InstallType == AppInstallType.Kiosk && scenario.Mode != ManagementMode.Dedicated)
            {
                skipped.Add($"app {app.Identifier}: Kiosk install type is only valid on dedicated devices");
                continue;
            }

            var entry = new JsonObject
            {
                ["packageName"] = app.Identifier,
                ["installType"] = app.InstallType switch
                {
                    AppInstallType.ForceInstalled => "FORCE_INSTALLED",
                    AppInstallType.Available => "AVAILABLE",
                    AppInstallType.Blocked => "BLOCKED",
                    _ => "KIOSK",
                },
            };
            var config = ParseManagedConfiguration(app.ManagedConfigurationJson);
            if (config is not null && app.InstallType != AppInstallType.Blocked)
            {
                entry["managedConfiguration"] = config;
                applied.Add($"app restrictions for {app.Identifier}");
            }

            applications.Add(entry);
            applied.Add($"app {app.Identifier} ({app.InstallType})");
        }

        // An always-on VPN client must be present for the VPN setting to work.
        if (vpnPackage is not null && apps.All(a => a.Identifier != vpnPackage))
        {
            applications.Add(new JsonObject { ["packageName"] = vpnPackage, ["installType"] = "FORCE_INSTALLED" });
            applied.Add($"app {vpnPackage} (ForceInstalled, required by VPN)");
        }

        if (settings.Kiosk is { AllowedPackages.Count: > 0 } kiosk && scenario.Mode == ManagementMode.Dedicated)
        {
            foreach (var package in kiosk.AllowedPackages.Where(p => !apps.Any(a => a.Identifier == p)))
            {
                applications.Add(new JsonObject { ["packageName"] = package, ["installType"] = "FORCE_INSTALLED" });
                applied.Add($"app {package} (ForceInstalled, kiosk allow-list)");
            }
        }

        if (applications.Count > 0)
        {
            policy["applications"] = applications;
        }

        policy["playStoreMode"] = settings.AppControl == AppControlMode.ApprovedAppsOnly ? "WHITELIST" : "BLACKLIST";
        applied.Add($"app control: {settings.AppControl}");

        // BYOD work profile: the personal side stays out of reach.
        if (workProfile)
        {
            var boundary = personal || settings.Restrictions.BlockManagedToPersonalSharing;
            if (boundary)
            {
                policy["crossProfilePolicies"] = new JsonObject
                {
                    ["crossProfileCopyPaste"] = "COPY_FROM_WORK_TO_PERSONAL_DISALLOWED",
                    ["crossProfileDataSharing"] = "DATA_SHARING_FROM_WORK_TO_PERSONAL_DISALLOWED",
                    ["showWorkContactsInPersonalProfile"] = "SHOW_WORK_CONTACTS_IN_PERSONAL_PROFILE_DISALLOWED",
                };
                applied.Add(personal
                    ? "corporate data kept out of personal apps (always enforced on personally owned devices)"
                    : "corporate data kept out of personal apps");
            }
        }
        else if (settings.Restrictions.BlockManagedToPersonalSharing)
        {
            skipped.Add("blockManagedToPersonalSharing: there is no personal profile on this device");
        }

        return new AndroidPolicyTranslation(policy, applied, skipped);
    }

    private static void TranslatePassword(JsonObject policy, ManagementScenario scenario, PasswordSettings? password, List<string> applied)
    {
        if (password is null)
        {
            return;
        }

        var rule = new JsonObject
        {
            ["passwordMinimumLength"] = password.MinLength,
            ["passwordQuality"] = password.Quality switch
            {
                PasswordQuality.Numeric => "NUMERIC",
                PasswordQuality.NumericComplex => "NUMERIC_COMPLEX",
                PasswordQuality.Alphabetic => "ALPHABETIC",
                PasswordQuality.Alphanumeric => "ALPHANUMERIC",
                PasswordQuality.Complex => "COMPLEX",
                _ => "SOMETHING",
            },
            // Work profile: the work challenge only. Fully managed/dedicated: the device lock.
            ["passwordScope"] = scenario.Mode == ManagementMode.WorkProfile ? "SCOPE_PROFILE" : "SCOPE_DEVICE",
        };
        if (password.MaxFailedAttempts is { } attempts)
        {
            rule["maximumFailedPasswordsForWipe"] = attempts;
        }

        if (password.ExpirationDays is { } days)
        {
            rule["passwordExpirationTimeout"] = $"{days * 86400L}s";
        }

        if (password.HistoryLength is { } history)
        {
            rule["passwordHistoryLength"] = history;
        }

        policy["passwordPolicies"] = new JsonArray(rule);
        if (password.MaxInactivitySeconds is { } idle)
        {
            policy["maximumTimeToLock"] = (idle * 1000L).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        applied.Add(scenario.Mode == ManagementMode.WorkProfile ? "password policy (work profile challenge)" : "password policy (device lock)");
    }

    private static void TranslateRestrictions(
        JsonObject policy,
        ManagementScenario scenario,
        RestrictionSettings restrictions,
        List<string> applied,
        List<string> skipped)
    {
        var scope = scenario.Mode == ManagementMode.WorkProfile ? " (work profile only)" : string.Empty;
        if (restrictions.DisableCamera)
        {
            policy["cameraDisabled"] = true;
            applied.Add("camera disabled" + scope);
        }

        if (restrictions.DisableScreenCapture)
        {
            policy["screenCaptureDisabled"] = true;
            applied.Add("screen capture disabled" + scope);
        }

        if (restrictions.DisableUsbFileTransfer)
        {
            if (scenario.IsPersonalBoundary)
            {
                skipped.Add("disableUsbFileTransfer: USB is controlled by the device owner; not applicable to a personally owned work profile");
            }
            else
            {
                policy["deviceConnectivityManagement"] = new JsonObject { ["usbDataAccess"] = "DISALLOW_USB_FILE_TRANSFER" };
                applied.Add("USB file transfer disabled");
            }
        }

        if (restrictions.DisableUnknownSources)
        {
            policy["advancedSecurityOverrides"] = new JsonObject { ["untrustedAppsPolicy"] = "DISALLOW_INSTALL" };
            applied.Add("installing from unknown sources disallowed" + scope);
        }

        if (restrictions.DisableFactoryReset)
        {
            if (scenario.Mode is ManagementMode.FullyManaged or ManagementMode.Dedicated)
            {
                policy["factoryResetDisabled"] = true;
                applied.Add("factory reset from settings disabled");
            }
            else
            {
                skipped.Add("disableFactoryReset: the user keeps control of device-level reset on work-profile devices");
            }
        }
    }

    private static void TranslateCompliance(JsonObject policy, ComplianceRequirements compliance, List<string> applied)
    {
        if (!TryParseMajor(compliance.MinimumOsVersion, out var major) || !ApiLevelByRelease.TryGetValue(major, out var apiLevel))
        {
            return;
        }

        policy["minimumApiLevel"] = apiLevel;
        applied.Add($"minimum Android version {major} (API {apiLevel})");
    }

    private static void TranslateNetworks(
        JsonObject policy,
        IReadOnlyList<WifiNetworkSettings> networks,
        Func<string?, string?> reveal,
        List<string> applied,
        List<string> skipped)
    {
        if (networks.Count == 0)
        {
            return;
        }

        var configurations = new JsonArray();
        var certificates = new JsonArray();
        foreach (var network in networks)
        {
            var wifi = new JsonObject
            {
                ["SSID"] = network.Ssid,
                ["AutoConnect"] = network.AutoJoin,
                ["HiddenSSID"] = network.Hidden,
            };
            switch (network.Security)
            {
                case WifiSecurity.Open:
                    wifi["Security"] = "None";
                    break;
                case WifiSecurity.WpaPersonal:
                    wifi["Security"] = "WPA-PSK";
                    wifi["Passphrase"] = reveal(network.Passphrase) ?? string.Empty;
                    break;
                default:
                    var guid = "{" + Guid.NewGuid().ToString() + "}";
                    var der = TryPemToBase64Der(network.ServerCaCertificatePem);
                    if (der is null)
                    {
                        skipped.Add($"wifi {network.Ssid}: enterprise network needs a valid server CA certificate");
                        continue;
                    }

                    certificates.Add(new JsonObject { ["GUID"] = guid, ["Type"] = "Authority", ["X509"] = der });
                    var eap = new JsonObject
                    {
                        ["Outer"] = "PEAP",
                        ["Inner"] = "MSCHAPv2",
                        ["UseSystemCAs"] = false,
                        ["ServerCARefs"] = new JsonArray(guid),
                    };
                    if (!string.IsNullOrWhiteSpace(network.AnonymousIdentity))
                    {
                        eap["AnonymousIdentity"] = network.AnonymousIdentity;
                    }

                    if (network.ServerNames.Count > 0)
                    {
                        eap["SubjectAlternativeNameMatch"] = new JsonArray(network.ServerNames
                            .Select(n => (JsonNode)new JsonObject { ["Type"] = "DNS", ["Value"] = n }).ToArray());
                    }

                    wifi["Security"] = "WPA-EAP";
                    wifi["EAP"] = eap;
                    break;
            }

            configurations.Add(new JsonObject
            {
                ["GUID"] = "{" + Guid.NewGuid().ToString() + "}",
                ["Name"] = network.Ssid,
                ["Type"] = "WiFi",
                ["WiFi"] = wifi,
            });
            applied.Add($"wifi {network.Ssid}");
        }

        if (configurations.Count == 0)
        {
            return;
        }

        var onc = new JsonObject
        {
            ["Type"] = "UnencryptedConfiguration",
            ["NetworkConfigurations"] = configurations,
        };
        if (certificates.Count > 0)
        {
            onc["Certificates"] = certificates;
        }

        policy["openNetworkConfiguration"] = onc;
    }

    private static string? TranslateVpn(JsonObject policy, IReadOnlyList<VpnSettings> vpn, List<string> applied, List<string> skipped)
    {
        string? package = null;
        foreach (var entry in vpn)
        {
            if (entry.Type != VpnType.AlwaysOnApp || string.IsNullOrWhiteSpace(entry.PackageName))
            {
                skipped.Add($"vpn {entry.Name}: Android supports always-on VPN apps only");
                continue;
            }

            if (package is not null)
            {
                skipped.Add($"vpn {entry.Name}: Android allows a single always-on VPN; '{package}' is already configured");
                continue;
            }

            package = entry.PackageName;
            policy["alwaysOnVpnPackage"] = new JsonObject
            {
                ["packageName"] = entry.PackageName,
                ["lockdownEnabled"] = entry.Lockdown,
            };
            applied.Add($"vpn {entry.Name} (always-on{(entry.Lockdown ? ", lockdown" : string.Empty)})");
        }

        return package;
    }

    private static void TranslateKiosk(
        JsonObject policy,
        ManagementScenario scenario,
        KioskSettings? kiosk,
        List<string> skipped,
        List<string> applied)
    {
        if (kiosk is null)
        {
            return;
        }

        if (scenario.Mode != ManagementMode.Dedicated)
        {
            skipped.Add("kiosk: only valid for dedicated devices");
            return;
        }

        if (kiosk.AllowedPackages.Count > 1)
        {
            policy["kioskCustomLauncherEnabled"] = true;
        }

        policy["kioskCustomization"] = new JsonObject
        {
            ["powerButtonActions"] = "POWER_BUTTON_AVAILABLE",
            ["systemErrorWarnings"] = "ERROR_AND_WARNINGS_MUTED",
            ["systemNavigation"] = "NAVIGATION_DISABLED",
            ["statusBar"] = kiosk.DisableStatusBar ? "NOTIFICATIONS_AND_SYSTEM_INFO_DISABLED" : "INFO_AND_NOTIFICATIONS_DISABLED",
            ["deviceSettings"] = "SETTINGS_ACCESS_BLOCKED",
        };
        policy["keyguardDisabled"] = kiosk.DisableKeyguard;
        if (kiosk.KeepScreenOn)
        {
            policy["stayOnPluggedModes"] = new JsonArray("AC", "USB", "WIRELESS");
        }

        applied.Add("kiosk mode");
    }

    private static JsonObject? ParseManagedConfiguration(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseMajor(string? version, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var head = version.Split('.', 2)[0];
        return int.TryParse(head, out major);
    }

    private static string? TryPemToBase64Der(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        try
        {
            using var cert = X509Certificate2.CreateFromPem(pem);
            return Convert.ToBase64String(cert.RawData);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}

public sealed record AndroidPolicyTranslation(JsonObject Policy, IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped);
