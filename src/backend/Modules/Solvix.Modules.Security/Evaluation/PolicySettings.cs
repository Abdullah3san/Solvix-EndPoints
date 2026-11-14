namespace Solvix.Modules.Security.Evaluation;

using Solvix.Modules.Security.Domain;

/// <summary>
/// The configurable part of a security policy. Versioned and immutable once submitted for approval.
/// Defaults describe a sensible baseline and are what a tenant gets before any policy is activated.
/// </summary>
public sealed record PolicySettings
{
    public const int MaxListItems = 100;
    public const int MaxEntryLength = 256;

    public EnforcementMode EnforcementMode { get; init; } = EnforcementMode.DetectOnly;

    /// <summary>Catalogue action keys Solvix may ask agents to run. Empty means none, even outside DetectOnly.</summary>
    public IReadOnlyList<string> AllowedRemediationActions { get; init; } = [];

    public bool RequireDefenderRealTimeProtection { get; init; } = true;

    public bool RequireDefenderTamperProtection { get; init; } = true;

    public int MaxSignatureAgeDays { get; init; } = 3;

    public int MaxDaysSinceQuickScan { get; init; } = 14;

    public bool RequireFirewallAllProfiles { get; init; } = true;

    public bool RequireBitLockerOsVolume { get; init; } = true;

    public bool RequireBitLockerFixedVolumes { get; init; }

    public int MaxLocalAdministrators { get; init; } = 3;

    /// <summary>
    /// Accounts/groups allowed in local Administrators (case-insensitive, matched on full name or SID).
    /// When empty the "unapproved administrator" check is not applicable rather than failing everyone.
    /// </summary>
    public IReadOnlyList<string> ApprovedLocalAdministrators { get; init; } = [];

    public bool RequireBuiltInAdministratorDisabled { get; init; } = true;

    public bool RequireGuestAccountDisabled { get; init; } = true;

    public UsbStoragePolicy UsbStorage { get; init; } = UsbStoragePolicy.Allow;

    public bool RequireSmb1Disabled { get; init; } = true;

    public bool RequireUac { get; init; } = true;

    public bool RequireRdpNla { get; init; } = true;

    public bool RequireSecureBoot { get; init; }

    public bool RequireWindowsUpdateNotDisabled { get; init; } = true;

    /// <summary>Failed-logon events within <see cref="FailedLogonWindowMinutes"/> that raise an alert.</summary>
    public int FailedLogonThreshold { get; init; } = 10;

    public int FailedLogonWindowMinutes { get; init; } = 10;

    /// <summary>A device that has not reported for this many days is Stale and its band is Unknown.</summary>
    public int StaleAfterDays { get; init; } = 7;

    public static PolicySettings Default { get; } = new();

    /// <summary>Returns the validation problems (property → message). Empty means valid.</summary>
    public IReadOnlyDictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>();
        if (!Enum.IsDefined(EnforcementMode))
        {
            errors[nameof(EnforcementMode)] = "Unknown enforcement mode.";
        }

        if (!Enum.IsDefined(UsbStorage))
        {
            errors[nameof(UsbStorage)] = "Unknown USB storage policy.";
        }

        Range(errors, nameof(MaxSignatureAgeDays), MaxSignatureAgeDays, 1, 30);
        Range(errors, nameof(MaxDaysSinceQuickScan), MaxDaysSinceQuickScan, 1, 90);
        Range(errors, nameof(MaxLocalAdministrators), MaxLocalAdministrators, 1, 50);
        Range(errors, nameof(FailedLogonThreshold), FailedLogonThreshold, 3, 1000);
        Range(errors, nameof(FailedLogonWindowMinutes), FailedLogonWindowMinutes, 1, 1440);
        Range(errors, nameof(StaleAfterDays), StaleAfterDays, 1, 90);

        ValidateList(errors, nameof(ApprovedLocalAdministrators), ApprovedLocalAdministrators);
        ValidateList(errors, nameof(AllowedRemediationActions), AllowedRemediationActions);
        foreach (var key in AllowedRemediationActions ?? [])
        {
            if (!RemediationCatalog.TryGet(key, out _))
            {
                errors[nameof(AllowedRemediationActions)] = $"'{key}' is not a known remediation action.";
                break;
            }
        }

        if (EnforcementMode == EnforcementMode.DetectOnly && AllowedRemediationActions is { Count: > 0 })
        {
            errors[nameof(AllowedRemediationActions)] = "DetectOnly policies cannot allow remediation actions.";
        }

        return errors;
    }

    private static void Range(Dictionary<string, string> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors[name] = $"Must be between {min} and {max}.";
        }
    }

    private static void ValidateList(Dictionary<string, string> errors, string name, IReadOnlyList<string>? list)
    {
        if (list is null)
        {
            errors[name] = "Must not be null.";
            return;
        }

        if (list.Count > MaxListItems)
        {
            errors[name] = $"At most {MaxListItems} entries.";
        }
        else if (list.Any(i => string.IsNullOrWhiteSpace(i) || i.Length > MaxEntryLength))
        {
            errors[name] = $"Entries must be non-empty and at most {MaxEntryLength} characters.";
        }
    }
}
