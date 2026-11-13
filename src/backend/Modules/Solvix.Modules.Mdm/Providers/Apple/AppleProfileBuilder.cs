namespace Solvix.Modules.Mdm.Providers.Apple;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Solvix.Modules.Mdm.Domain;

/// <summary>
/// Builds Apple configuration profiles. The enrollment profile bootstraps MDM; the policy profile is the
/// translation of a Solvix policy for one scenario. As on Android, anything the scenario's boundary
/// forbids is not emitted and is reported.
/// </summary>
public static class AppleProfileBuilder
{
    /// <summary>
    /// One policy profile identifier per device: installing a profile with an existing identifier replaces
    /// it, so reassigning a device to another policy never leaves the previous policy installed.
    /// </summary>
    public const string PolicyProfileIdentifier = "com.solvix.mdm.policy";

    public const string EnrollmentIdentifierPrefix = "com.solvix.mdm.enrollment.";

    public static string EnrollmentIdentifier(string handle) => EnrollmentIdentifierPrefix + handle;

    public static byte[] BuildEnrollmentProfile(AppleEnrollmentProfileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var scenario = options.Scenario.EnsureValid();
        if (scenario.Platform != MobilePlatform.Ios)
        {
            throw new ArgumentException("Not an Apple scenario.", nameof(options));
        }

        var scepUuid = DeterministicUuid(options.Handle, "scep");
        var mdmUuid = DeterministicUuid(options.Handle, "mdm");
        var rootUuid = DeterministicUuid(options.Handle, "profile");

        var scep = new Dictionary<string, object?>
        {
            ["PayloadType"] = "com.apple.security.scep",
            ["PayloadVersion"] = 1L,
            ["PayloadIdentifier"] = EnrollmentIdentifier(options.Handle) + ".scep",
            ["PayloadUUID"] = scepUuid,
            ["PayloadDisplayName"] = "Device identity",
            ["PayloadContent"] = new Dictionary<string, object?>
            {
                ["URL"] = options.ScepUrl,
                ["Name"] = "Solvix MDM",
                ["Subject"] = new List<object?> { new List<object?> { new List<object?> { "CN", "%HardwareUUID%" } } },
                ["Challenge"] = options.ScepChallenge ?? string.Empty,
                ["Keysize"] = 2048L,
                ["KeyType"] = "RSA",
                ["KeyUsage"] = 5L,
                ["Retries"] = 3L,
                ["RetryDelay"] = 10L,
            },
        };

        var mdm = new Dictionary<string, object?>
        {
            ["PayloadType"] = "com.apple.mdm",
            ["PayloadVersion"] = 1L,
            ["PayloadIdentifier"] = EnrollmentIdentifier(options.Handle) + ".mdm",
            ["PayloadUUID"] = mdmUuid,
            ["PayloadDisplayName"] = "Mobile device management",
            ["IdentityCertificateUUID"] = scepUuid,
            ["Topic"] = options.PushTopic,
            ["ServerURL"] = $"{options.BaseUrl.TrimEnd('/')}/api/v1/mdm/apple/{options.Handle}/server",
            ["CheckInURL"] = $"{options.BaseUrl.TrimEnd('/')}/api/v1/mdm/apple/{options.Handle}/checkin",
            ["CheckOutWhenRemoved"] = true,
            ["SignMessage"] = true,
            ["ServerCapabilities"] = new List<object?> { "com.apple.mdm.per-user-connections" },
        };

        if (scenario.Mode == ManagementMode.UserEnrollment)
        {
            // BYOD: no access rights over the device; the profile only enrols the managed account/data space.
            mdm["EnrollmentMode"] = "BYOD";
        }
        else
        {
            // Inspect device and installed profiles/apps, install/remove profiles and apps, lock and erase, manage settings.
            mdm["AccessRights"] = 8191L;
        }

        return ApplePlist.Serialize(new Dictionary<string, object?>
        {
            ["PayloadType"] = "Configuration",
            ["PayloadVersion"] = 1L,
            ["PayloadIdentifier"] = EnrollmentIdentifier(options.Handle),
            ["PayloadUUID"] = rootUuid,
            ["PayloadDisplayName"] = options.OrganizationName + " enrollment",
            ["PayloadOrganization"] = options.OrganizationName,
            ["PayloadDescription"] = scenario.IsPersonalBoundary
                ? "Enrolls this device for access to work apps and data. Your personal data stays private."
                : "Enrolls this device in mobile device management.",
            ["PayloadRemovalDisallowed"] = false,
            ["PayloadContent"] = new List<object?> { scep, mdm },
        });
    }

    public static AppleProfileTranslation BuildPolicyProfile(
        Guid policyId,
        string name,
        int version,
        ManagementScenario scenario,
        MobilePolicySettings settings,
        IReadOnlyList<PolicyAppSpec> apps,
        Func<string?, string?> revealSecret)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(revealSecret);
        scenario.EnsureValid();
        if (scenario.Platform != MobilePlatform.Ios)
        {
            throw new ArgumentException("Not an Apple scenario.", nameof(scenario));
        }

        var applied = new List<string>();
        var skipped = new List<string>();
        var payloads = new List<object?>();
        var identifier = PolicyProfileIdentifier;
        var seed = $"{PolicyProfileIdentifier}.{policyId:N}";
        var userEnrollment = scenario.Mode == ManagementMode.UserEnrollment;
        var supervised = scenario.Mode == ManagementMode.AutomatedDeviceEnrollment;
        int counter = 0;

        Dictionary<string, object?> Payload(string type, string display)
        {
            counter++;
            return new Dictionary<string, object?>
            {
                ["PayloadType"] = type,
                ["PayloadVersion"] = 1L,
                ["PayloadIdentifier"] = $"{identifier}.{counter}",
                ["PayloadUUID"] = DeterministicUuid(seed, counter.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ["PayloadDisplayName"] = display,
            };
        }

        // ---- passcode
        if (settings.Password is { } password)
        {
            var p = Payload("com.apple.mobiledevice.passwordpolicy", "Passcode");
            p["forcePIN"] = true;
            p["minLength"] = (long)password.MinLength;
            p["allowSimple"] = password.Quality is PasswordQuality.Required or PasswordQuality.Numeric or PasswordQuality.Alphabetic;
            p["requireAlphanumeric"] = password.Quality is PasswordQuality.Alphabetic or PasswordQuality.Alphanumeric or PasswordQuality.Complex;
            if (password.Quality == PasswordQuality.Complex)
            {
                p["minComplexChars"] = 1L;
            }

            if (password.ExpirationDays is { } days)
            {
                p["maxPINAgeInDays"] = (long)days;
            }

            if (password.HistoryLength is { } history)
            {
                p["pinHistory"] = (long)history;
            }

            if (password.MaxInactivitySeconds is { } idle)
            {
                p["maxInactivity"] = (long)Math.Max(1, (int)Math.Ceiling(idle / 60.0));
            }

            if (password.MaxFailedAttempts is { } attempts)
            {
                if (userEnrollment)
                {
                    skipped.Add("maxFailedAttempts: erasing on failed attempts is not permitted on User Enrollment devices");
                }
                else
                {
                    p["maxFailedAttempts"] = (long)Math.Clamp(attempts, 2, 11);
                }
            }

            payloads.Add(p);
            applied.Add(userEnrollment ? "passcode policy (limited by User Enrollment)" : "passcode policy");
        }

        // ---- Wi-Fi
        foreach (var network in settings.WifiNetworks)
        {
            var p = Payload("com.apple.wifi.managed", $"Wi-Fi {network.Ssid}");
            p["SSID_STR"] = network.Ssid;
            p["HIDDEN_NETWORK"] = network.Hidden;
            p["AutoJoin"] = network.AutoJoin;
            switch (network.Security)
            {
                case WifiSecurity.Open:
                    p["EncryptionType"] = "None";
                    break;
                case WifiSecurity.WpaPersonal:
                    p["EncryptionType"] = "WPA";
                    p["Password"] = revealSecret(network.Passphrase) ?? string.Empty;
                    break;
                default:
                    var certificate = LoadCertificate(network.ServerCaCertificatePem);
                    if (certificate is null)
                    {
                        skipped.Add($"wifi {network.Ssid}: enterprise network needs a valid server CA certificate");
                        counter--;
                        continue;
                    }

                    var root = Payload("com.apple.security.root", $"Wi-Fi CA {network.Ssid}");
                    root["PayloadContent"] = certificate.RawData;
                    payloads.Add(root);
                    var eap = new Dictionary<string, object?>
                    {
                        ["AcceptEAPTypes"] = new List<object?> { 25L },
                        ["TLSAllowTrustExceptions"] = false,
                    };
                    if (network.ServerNames.Count > 0)
                    {
                        eap["TLSTrustedServerNames"] = network.ServerNames.Cast<object?>().ToList();
                    }

                    if (!string.IsNullOrWhiteSpace(network.AnonymousIdentity))
                    {
                        eap["OuterIdentity"] = network.AnonymousIdentity;
                    }

                    p["EncryptionType"] = "WPA";
                    p["EAPClientConfiguration"] = eap;
                    p["PayloadCertificateAnchorUUID"] = new List<object?> { root["PayloadUUID"] };
                    certificate.Dispose();
                    break;
            }

            payloads.Add(p);
            applied.Add($"wifi {network.Ssid}");
        }

        // ---- VPN
        foreach (var vpn in settings.Vpn)
        {
            if (vpn.Type != VpnType.IkeV2 || string.IsNullOrWhiteSpace(vpn.Server))
            {
                skipped.Add($"vpn {vpn.Name}: iOS supports IKEv2 configurations only");
                continue;
            }

            if (userEnrollment)
            {
                skipped.Add($"vpn {vpn.Name}: User Enrollment only allows per-app VPN, which is not supported yet");
                continue;
            }

            var p = Payload("com.apple.vpn.managed", $"VPN {vpn.Name}");
            p["UserDefinedName"] = vpn.Name;
            p["VPNType"] = "IKEv2";
            var ike = new Dictionary<string, object?>
            {
                ["RemoteAddress"] = vpn.Server,
                ["RemoteIdentifier"] = vpn.RemoteIdentifier ?? vpn.Server,
                ["AuthenticationMethod"] = "SharedSecret",
                ["ExtendedAuthEnabled"] = 0L,
                ["DeadPeerDetectionRate"] = "Medium",
            };
            if (!string.IsNullOrWhiteSpace(vpn.LocalIdentifier))
            {
                ike["LocalIdentifier"] = vpn.LocalIdentifier;
            }

            if (revealSecret(vpn.SharedSecret) is { } secret)
            {
                ike["SharedSecret"] = Encoding.UTF8.GetBytes(secret);
            }

            p["IKEv2"] = ike;
            payloads.Add(p);
            applied.Add($"vpn {vpn.Name} (IKEv2)");
        }

        // ---- restrictions
        var restrictions = BuildRestrictions(scenario, settings, apps, supervised, userEnrollment, applied, skipped);
        if (restrictions.Count > 0)
        {
            var p = Payload("com.apple.applicationaccess", "Restrictions");
            foreach (var (k, v) in restrictions)
            {
                p[k] = v;
            }

            payloads.Add(p);
        }

        if (settings.Kiosk is not null)
        {
            skipped.Add("kiosk: dedicated-device mode is Android-only; Single App Mode is not implemented for iOS");
        }

        var profile = new Dictionary<string, object?>
        {
            ["PayloadType"] = "Configuration",
            ["PayloadVersion"] = 1L,
            ["PayloadIdentifier"] = identifier,
            ["PayloadUUID"] = DeterministicUuid(seed, "root:" + version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ["PayloadDisplayName"] = name,
            ["PayloadDescription"] = $"Managed by Solvix (policy version {version}).",
            ["PayloadOrganization"] = "Solvix EndPoints",
            ["PayloadRemovalDisallowed"] = false,
            ["PayloadContent"] = payloads,
        };
        return new AppleProfileTranslation(ApplePlist.Serialize(profile), identifier, applied, skipped);
    }

    private static Dictionary<string, object?> BuildRestrictions(
        ManagementScenario scenario,
        MobilePolicySettings settings,
        IReadOnlyList<PolicyAppSpec> apps,
        bool supervised,
        bool userEnrollment,
        List<string> applied,
        List<string> skipped)
    {
        var r = new Dictionary<string, object?>();
        var restrictions = settings.Restrictions;

        void DeviceWide(bool wanted, string label, Action emit, bool needsSupervision = false)
        {
            if (!wanted)
            {
                return;
            }

            if (userEnrollment)
            {
                skipped.Add($"{label}: device-wide restrictions are not available on User Enrollment (personal device)");
            }
            else if (needsSupervision && !supervised)
            {
                skipped.Add($"{label}: requires a supervised device");
            }
            else
            {
                emit();
                applied.Add(label);
            }
        }

        DeviceWide(restrictions.DisableCamera, "camera disabled", () => r["allowCamera"] = false);
        DeviceWide(restrictions.DisableScreenCapture, "screen capture disabled", () => r["allowScreenShot"] = false);
        DeviceWide(restrictions.DisableFactoryReset, "erase content and settings disabled", () => r["allowEraseContentAndSettings"] = false, needsSupervision: true);

        if (restrictions.DisableUsbFileTransfer)
        {
            skipped.Add("disableUsbFileTransfer: iOS exposes no USB file transfer to restrict");
        }

        if (restrictions.DisableUnknownSources)
        {
            skipped.Add("disableUnknownSources: not applicable to iOS");
        }

        if (scenario.IsPersonalBoundary || restrictions.BlockManagedToPersonalSharing)
        {
            r["allowOpenFromManagedToUnmanaged"] = false;
            r["allowManagedToWriteUnmanagedContacts"] = false;
            applied.Add(scenario.IsPersonalBoundary
                ? "managed data kept out of personal apps (always enforced on personally owned devices)"
                : "managed data kept out of personal apps");
        }

        var blocked = apps.Where(a => a.InstallType == AppInstallType.Blocked).Select(a => a.Identifier).ToList();
        if (blocked.Count > 0)
        {
            if (supervised)
            {
                r["blacklistedAppBundleIDs"] = blocked.Cast<object?>().ToList();
                applied.Add($"blocked apps: {string.Join(", ", blocked)}");
            }
            else
            {
                skipped.Add("blocked apps: hiding/blocking installed apps requires a supervised device");
            }
        }

        if (settings.AppControl == AppControlMode.ApprovedAppsOnly)
        {
            if (supervised)
            {
                var allowed = apps.Where(a => a.InstallType is AppInstallType.ForceInstalled or AppInstallType.Available).Select(a => a.Identifier).ToList();
                r["whitelistedAppBundleIDs"] = allowed.Cast<object?>().ToList();
                applied.Add("approved apps only");
            }
            else
            {
                skipped.Add("approved apps only: restricting which apps may run requires a supervised device");
            }
        }

        return r;
    }

    private static X509Certificate2? LoadCertificate(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        try
        {
            return X509Certificate2.CreateFromPem(pem);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Stable UUID so the same logical payload keeps its identity across rebuilds.</summary>
    private static string DeterministicUuid(string scope, string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}|{name}"));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString().ToUpperInvariant();
    }
}

public sealed record AppleEnrollmentProfileOptions(
    string Handle,
    ManagementScenario Scenario,
    string BaseUrl,
    string PushTopic,
    string ScepUrl,
    string? ScepChallenge,
    string OrganizationName);

public sealed record AppleProfileTranslation(
    byte[] Profile,
    string Identifier,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped);
