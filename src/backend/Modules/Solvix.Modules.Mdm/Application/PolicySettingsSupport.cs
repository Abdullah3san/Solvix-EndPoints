namespace Solvix.Modules.Mdm.Application;

using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Application.Compliance;
using Solvix.Modules.Mdm.Security;
using Solvix.SharedKernel.Exceptions;

/// <summary>Scenario-aware validation, secret protection and redaction of policy settings.</summary>
internal static class PolicySettingsSupport
{
    public static void Validate(ManagementScenario scenario, MobilePolicySettings settings)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                errors[key] = list = [];
            }

            list.Add(message);
        }

        if (settings.Password is { } p)
        {
            if (p.MinLength is < 4 or > 64)
            {
                Add("settings.password.minLength", "Minimum length must be between 4 and 64.");
            }

            if (p.MaxFailedAttempts is < 2 or > 20)
            {
                Add("settings.password.maxFailedAttempts", "Maximum failed attempts must be between 2 and 20.");
            }

            if (p.MaxInactivitySeconds is < 15 or > 3600)
            {
                Add("settings.password.maxInactivitySeconds", "Auto-lock must be between 15 and 3600 seconds.");
            }

            if (p.ExpirationDays is < 1 or > 730)
            {
                Add("settings.password.expirationDays", "Expiration must be between 1 and 730 days.");
            }

            if (p.HistoryLength is < 1 or > 50)
            {
                Add("settings.password.historyLength", "History length must be between 1 and 50.");
            }
        }

        if (settings.WifiNetworks.Count > 50)
        {
            Add("settings.wifiNetworks", "At most 50 Wi-Fi networks per policy.");
        }

        for (var i = 0; i < settings.WifiNetworks.Count; i++)
        {
            var w = settings.WifiNetworks[i];
            var key = $"settings.wifiNetworks[{i}]";
            if (string.IsNullOrWhiteSpace(w.Ssid) || w.Ssid.Length > 32)
            {
                Add(key + ".ssid", "SSID is required and at most 32 characters.");
            }

            if (!string.IsNullOrEmpty(w.Passphrase) && w.Passphrase.Length is < 8 or > 63)
            {
                Add(key + ".passphrase", "A WPA passphrase must be 8-63 characters.");
            }

            if (w.Security == WifiSecurity.WpaEnterprisePeap && !IsCertificate(w.ServerCaCertificatePem))
            {
                Add(key + ".serverCaCertificatePem", "Enterprise Wi-Fi requires the PEM certificate of the RADIUS server's certificate authority.");
            }
        }

        for (var i = 0; i < settings.Vpn.Count; i++)
        {
            var v = settings.Vpn[i];
            var key = $"settings.vpn[{i}]";
            if (string.IsNullOrWhiteSpace(v.Name) || v.Name.Length > 100)
            {
                Add(key + ".name", "Name is required and at most 100 characters.");
            }

            if (scenario.Platform == MobilePlatform.Android && v.Type != VpnType.AlwaysOnApp)
            {
                Add(key + ".type", "Android supports always-on VPN apps only.");
            }

            if (scenario.Platform == MobilePlatform.Ios && v.Type != VpnType.IkeV2)
            {
                Add(key + ".type", "iOS supports IKEv2 only.");
            }

            if (v.Type == VpnType.AlwaysOnApp && string.IsNullOrWhiteSpace(v.PackageName))
            {
                Add(key + ".packageName", "The VPN app package name is required.");
            }

            if (v.Type == VpnType.IkeV2 && string.IsNullOrWhiteSpace(v.Server))
            {
                Add(key + ".server", "The VPN gateway host name is required.");
            }
        }

        if (settings.Vpn.Count(v => v.Type == VpnType.AlwaysOnApp) > 1)
        {
            Add("settings.vpn", "Android allows a single always-on VPN.");
        }

        if (settings.Kiosk is not null)
        {
            if (scenario.Mode != ManagementMode.Dedicated)
            {
                Add("settings.kiosk", "Kiosk settings are only valid on dedicated devices.");
            }
            else if (settings.Kiosk.AllowedPackages.Count == 0 || settings.Kiosk.AllowedPackages.Any(string.IsNullOrWhiteSpace))
            {
                Add("settings.kiosk.allowedPackages", "At least one allowed package is required.");
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.Compliance.MinimumOsVersion) && ComplianceEvaluator.TryCompare(settings.Compliance.MinimumOsVersion, "0.0") is null)
        {
            Add("settings.compliance.minimumOsVersion", "Minimum OS version must look like 13 or 17.4.");
        }

        if (settings.Compliance.MaxHoursSinceCheckIn is < 1 or > 24 * 90)
        {
            Add("settings.compliance.maxHoursSinceCheckIn", "Must be between 1 and 2160 hours.");
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
        }
    }

    /// <summary>
    /// Encrypts newly supplied secrets. A network or VPN that already existed keeps its stored secret when the
    /// request omits it, so editing a policy never requires re-entering every passphrase.
    /// </summary>
    public static MobilePolicySettings Protect(MobilePolicySettings incoming, MobilePolicySettings? existing, ISecretProtector protector)
    {
        var wifi = incoming.WifiNetworks.Select(n =>
        {
            var previous = existing?.WifiNetworks.FirstOrDefault(e => e.Ssid == n.Ssid && e.Security == n.Security);
            var secret = !string.IsNullOrEmpty(n.Passphrase) ? protector.Protect(n.Passphrase) : previous?.Passphrase;
            return n with { Passphrase = n.Security == WifiSecurity.WpaPersonal ? secret : null, PassphraseSet = false };
        }).ToList();
        var vpn = incoming.Vpn.Select(v =>
        {
            var previous = existing?.Vpn.FirstOrDefault(e => e.Name == v.Name);
            var secret = !string.IsNullOrEmpty(v.SharedSecret) ? protector.Protect(v.SharedSecret) : previous?.SharedSecret;
            return v with { SharedSecret = v.Type == VpnType.IkeV2 ? secret : null, SharedSecretSet = false };
        }).ToList();
        return incoming with { WifiNetworks = wifi, Vpn = vpn };
    }

    /// <summary>After <see cref="Protect"/>: every network/VPN that needs a secret must now have one stored.</summary>
    public static void EnsureSecrets(MobilePolicySettings protectedSettings)
    {
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < protectedSettings.WifiNetworks.Count; i++)
        {
            if (protectedSettings.WifiNetworks[i] is { Security: WifiSecurity.WpaPersonal, Passphrase: null or "" })
            {
                errors[$"settings.wifiNetworks[{i}].passphrase"] = ["A WPA passphrase of 8-63 characters is required."];
            }
        }

        for (var i = 0; i < protectedSettings.Vpn.Count; i++)
        {
            if (protectedSettings.Vpn[i] is { Type: VpnType.IkeV2, SharedSecret: null or "" })
            {
                errors[$"settings.vpn[{i}].sharedSecret"] = ["A shared secret is required."];
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }

    /// <summary>Strips secret values for API responses, keeping a flag that tells whether one is stored.</summary>
    public static MobilePolicySettings Redact(MobilePolicySettings stored) => stored with
    {
        WifiNetworks = stored.WifiNetworks.Select(n => n with { Passphrase = null, PassphraseSet = !string.IsNullOrEmpty(n.Passphrase) }).ToList(),
        Vpn = stored.Vpn.Select(v => v with { SharedSecret = null, SharedSecretSet = !string.IsNullOrEmpty(v.SharedSecret) }).ToList(),
    };

    public static bool IsCertificate(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return false;
        }

        try
        {
            using var cert = X509Certificate2.CreateFromPem(pem);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Managed configuration must be a flat JSON object of strings, numbers and booleans.</summary>
    public static string? NormalizeManagedConfiguration(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || doc.RootElement.EnumerateObject().Any(p => p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
            {
                throw new ValidationException(property, "Managed configuration must be a flat JSON object.");
            }

            return JsonSerializer.Serialize(doc.RootElement);
        }
        catch (JsonException)
        {
            throw new ValidationException(property, "Managed configuration is not valid JSON.");
        }
    }
}
