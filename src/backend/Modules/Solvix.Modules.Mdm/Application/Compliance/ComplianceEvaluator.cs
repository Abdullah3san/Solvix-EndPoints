namespace Solvix.Modules.Mdm.Application.Compliance;

using System.Globalization;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers;

public sealed record ComplianceResult(ComplianceState State, IReadOnlyList<ComplianceViolation> Violations, IReadOnlyList<string> Notes);

/// <summary>
/// Pure compliance evaluation of a device against its policy's requirements. A missing signal never
/// counts as a violation (it is reported in <see cref="ComplianceResult.Notes"/>), so platform limits such as
/// iOS having no jailbreak indicator do not make every iPhone non-compliant.
/// </summary>
public static class ComplianceEvaluator
{
    public static ComplianceResult Evaluate(MobileDevice device, MobilePolicy? policy, IReadOnlyList<PolicyAppSpec> apps, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(apps);
        var violations = new List<ComplianceViolation>();
        var notes = new List<string>();

        if (device.State is DeviceState.Pending or DeviceState.Retired or DeviceState.Wiped)
        {
            return new ComplianceResult(ComplianceState.Unknown, [], [$"Device is {device.State}; compliance is not evaluated."]);
        }

        if (policy is null)
        {
            return new ComplianceResult(ComplianceState.Unknown, [], ["No policy is assigned, so there are no requirements to evaluate."]);
        }

        var rules = policy.Settings.Compliance;
        if (device.LastSeenAtUtc is null || (device.OsVersion is null && device.Platform == MobilePlatform.Android))
        {
            return new ComplianceResult(ComplianceState.Unknown, [], ["The device has not reported inventory yet."]);
        }

        if (!string.IsNullOrWhiteSpace(rules.MinimumOsVersion))
        {
            if (string.IsNullOrWhiteSpace(device.OsVersion))
            {
                notes.Add("OS version not reported.");
            }
            else if (TryCompare(device.OsVersion, rules.MinimumOsVersion) is { } cmp && cmp < 0)
            {
                violations.Add(new("minimum_os_version", $"OS version {device.OsVersion} is older than the required {rules.MinimumOsVersion}."));
            }
        }

        Require(rules.RequirePasscode, device.HasPasscode, "passcode_required", "No device passcode is set.", "Passcode state not reported.", violations, notes);
        Require(rules.RequireEncryption, device.IsEncrypted, "encryption_required", "Device storage is not encrypted.", "Encryption state not reported.", violations, notes);

        if (rules.BlockCompromisedDevices)
        {
            if (device.Integrity == IntegrityState.Compromised)
            {
                violations.Add(new("compromised_device", "The device is rooted, jailbroken or otherwise reported as compromised."));
            }
            else if (device.Integrity == IntegrityState.Unknown)
            {
                notes.Add("This platform does not report device integrity; the compromised-device rule was not evaluated.");
            }
        }

        if (rules.MaxHoursSinceCheckIn is { } hours && device.LastSeenAtUtc is { } seen && now - seen > TimeSpan.FromHours(hours))
        {
            violations.Add(new("stale_check_in", $"The device has not contacted the server for more than {hours} hours."));
        }

        if (rules.RequireAssignedPolicyApplied && device.AppliedPolicyVersion != policy.PublishedVersion)
        {
            violations.Add(new("policy_not_applied", $"The device has not applied policy version {policy.PublishedVersion}."));
        }

        if (device.AppInventoryAtUtc is not null)
        {
            var installedIds = device.InstalledApps.Select(a => a.Identifier).ToHashSet(StringComparer.Ordinal);
            foreach (var app in apps)
            {
                if (app.InstallType == AppInstallType.ForceInstalled && !installedIds.Contains(app.Identifier))
                {
                    violations.Add(new("required_app_missing", $"Required app {app.Name} ({app.Identifier}) is not installed."));
                }
                else if (app.InstallType == AppInstallType.Blocked && installedIds.Contains(app.Identifier))
                {
                    violations.Add(new("blocked_app_installed", $"Blocked app {app.Name} ({app.Identifier}) is installed."));
                }
            }
        }

        return new ComplianceResult(
            violations.Count > 0 ? ComplianceState.NonCompliant : ComplianceState.Compliant,
            violations,
            notes);
    }

    /// <summary>Compares dotted versions numerically; returns null if either side cannot be parsed.</summary>
    public static int? TryCompare(string actual, string required)
    {
        if (!TryParse(actual, out var a) || !TryParse(required, out var r))
        {
            return null;
        }

        return a.CompareTo(r);
    }

    private static bool TryParse(string value, out Version version)
    {
        var parts = value.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new string(p.TakeWhile(char.IsAsciiDigit).ToArray()))
            .Where(p => p.Length > 0)
            .Take(4)
            .Select(p => int.Parse(p, CultureInfo.InvariantCulture))
            .ToList();
        while (parts.Count < 2)
        {
            parts.Add(0);
        }

        version = new Version(parts[0], parts[1], parts.Count > 2 ? parts[2] : 0, parts.Count > 3 ? parts[3] : 0);
        return parts.Count >= 2 && value.Trim().Length > 0 && char.IsAsciiDigit(value.Trim()[0]);
    }

    private static void Require(bool required, bool? signal, string rule, string message, string missing, List<ComplianceViolation> violations, List<string> notes)
    {
        if (!required)
        {
            return;
        }

        if (signal is null)
        {
            notes.Add(missing);
        }
        else if (!signal.Value)
        {
            violations.Add(new(rule, message));
        }
    }
}
