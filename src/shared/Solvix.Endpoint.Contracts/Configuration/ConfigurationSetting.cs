namespace Solvix.Endpoint.Contracts.Configuration;

using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// A single desired-state item. Settings are immutable values; two settings with
/// the same <see cref="Key"/> target the same thing on the device, which is how
/// conflicts between policies and drift are detected.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ServiceSetting), "Service")]
[JsonDerivedType(typeof(RegistrySetting), "Registry")]
[JsonDerivedType(typeof(StartupAppSetting), "StartupApp")]
[JsonDerivedType(typeof(FirewallProfileSetting), "FirewallProfile")]
[JsonDerivedType(typeof(FirewallRuleSetting), "FirewallRule")]
[JsonDerivedType(typeof(PowerSetting), "Power")]
[JsonDerivedType(typeof(ScreenLockSetting), "ScreenLock")]
[JsonDerivedType(typeof(UsbStorageSetting), "UsbStorage")]
public abstract record ConfigurationSetting
{
    [JsonIgnore]
    public abstract SettingKind Kind { get; }

    /// <summary>Stable, lower-case identity of the target on the device, e.g. <c>service:spooler</c>.</summary>
    [JsonIgnore]
    public abstract string Key { get; }

    /// <summary>Returns human-readable validation errors; empty when valid.</summary>
    public abstract IReadOnlyList<string> Validate();

    /// <summary>Short description for UIs, audit entries and agent logs.</summary>
    public abstract string Describe();

    private protected static string Lower(string value) => value.Trim().ToLowerInvariant();
}

/// <summary>Windows service startup type and/or run state (Service Control Manager).</summary>
public sealed record ServiceSetting(string ServiceName, ServiceStartupType? StartupType, ServiceRunState? State) : ConfigurationSetting
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9_.$-]{1,256}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [JsonIgnore]
    public override SettingKind Kind => SettingKind.Service;

    [JsonIgnore]
    public override string Key => $"service:{Lower(ServiceName ?? string.Empty)}";

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ServiceName) || !NamePattern.IsMatch(ServiceName))
        {
            errors.Add("serviceName must be the service key name (letters, digits, '.', '_', '-', '$').");
        }

        if (StartupType is null && State is null)
        {
            errors.Add("Specify startupType, state or both.");
        }

        if (StartupType == ServiceStartupType.Disabled && State == ServiceRunState.Running)
        {
            errors.Add("A disabled service cannot be required to run.");
        }

        return errors;
    }

    public override string Describe() =>
        $"Service '{ServiceName}'" +
        (StartupType is { } s ? $" startup={s}" : string.Empty) +
        (State is { } r ? $" state={r}" : string.Empty);
}

/// <summary>A value under HKEY_LOCAL_MACHINE.</summary>
public sealed record RegistrySetting(
    string Path,
    string ValueName,
    RegistryValueType ValueType,
    string? Value,
    PresenceEnsure Ensure = PresenceEnsure.Present) : ConfigurationSetting
{
    public const int MaxPathLength = 512;

    [JsonIgnore]
    public override SettingKind Kind => SettingKind.Registry;

    [JsonIgnore]
    public override string Key => $"registry:hklm\\{NormalizePath(Path)}\\{(string.IsNullOrEmpty(ValueName) ? "(default)" : Lower(ValueName))}";

    /// <summary>Trims, removes an <c>HKLM\</c>/<c>HKEY_LOCAL_MACHINE\</c> prefix and collapses separators.</summary>
    public static string NormalizePath(string? path)
    {
        var p = (path ?? string.Empty).Trim().Replace('/', '\\');
        foreach (var prefix in new[] { "HKEY_LOCAL_MACHINE\\", "HKLM\\", "HKLM:\\" })
        {
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                p = p[prefix.Length..];
            }
        }

        while (p.Contains("\\\\", StringComparison.Ordinal))
        {
            p = p.Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return p.Trim('\\').ToLowerInvariant();
    }

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var path = NormalizePath(Path);
        if (path.Length == 0 || path.Length > MaxPathLength)
        {
            errors.Add($"path is required and must be at most {MaxPathLength} characters (relative to HKEY_LOCAL_MACHINE).");
        }

        if (path.Split('\\').Any(s => s.Length == 0 || s is "." or ".."))
        {
            errors.Add("path contains an empty or relative segment.");
        }

        if (ValueName is { Length: > 255 })
        {
            errors.Add("valueName must be at most 255 characters.");
        }

        if (Ensure == PresenceEnsure.Present)
        {
            if (Value is null)
            {
                errors.Add("value is required when ensure is Present.");
            }
            else if (!TryNormalizeValue(ValueType, Value, out _))
            {
                errors.Add($"value is not a valid {ValueType}.");
            }
        }

        return errors;
    }

    /// <summary>Canonical text form of a value of the given type, used for comparison and drift detection.</summary>
    public static bool TryNormalizeValue(RegistryValueType type, string value, out string normalized)
    {
        ArgumentNullException.ThrowIfNull(value);
        normalized = string.Empty;
        switch (type)
        {
            case RegistryValueType.String:
            case RegistryValueType.ExpandString:
                normalized = value;
                return true;
            case RegistryValueType.MultiString:
                normalized = string.Join('\n', value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
                return true;
            case RegistryValueType.DWord:
                if (TryParseUnsigned(value, uint.MaxValue, out var dword))
                {
                    normalized = dword.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                return false;
            case RegistryValueType.QWord:
                if (TryParseUnsigned(value, ulong.MaxValue, out var qword))
                {
                    normalized = qword.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                return false;
            case RegistryValueType.Binary:
                var hex = value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
                if (hex.Length % 2 != 0 || hex.Any(c => !char.IsAsciiHexDigit(c)))
                {
                    return false;
                }

                normalized = hex.ToUpperInvariant();
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseUnsigned(string value, ulong max, out ulong result)
    {
        var v = value.Trim();
        var ok = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(v[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result)
            : ulong.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out result);
        return ok && result <= max;
    }

    public override string Describe() =>
        Ensure == PresenceEnsure.Absent
            ? $"Delete HKLM\\{NormalizePath(Path)}\\{ValueName}"
            : $"HKLM\\{NormalizePath(Path)}\\{ValueName} = {Value} ({ValueType})";
}

/// <summary>A machine-wide "Run" entry.</summary>
public sealed record StartupAppSetting(string Name, StartupLocation Location, string? Command, StartupEnsure Ensure) : ConfigurationSetting
{
    [JsonIgnore]
    public override SettingKind Kind => SettingKind.StartupApp;

    [JsonIgnore]
    public override string Key => $"startup:{Location.ToString().ToLowerInvariant()}:{Lower(Name ?? string.Empty)}";

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 255 || Name.Contains('\\', StringComparison.Ordinal))
        {
            errors.Add("name is required, at most 255 characters and cannot contain '\\'.");
        }

        if (Ensure == StartupEnsure.Present && string.IsNullOrWhiteSpace(Command))
        {
            errors.Add("command is required when ensure is Present.");
        }

        if (Command is { Length: > 2048 })
        {
            errors.Add("command must be at most 2048 characters.");
        }

        if (Location == StartupLocation.MachineRunOnce && Ensure == StartupEnsure.Disabled)
        {
            errors.Add("RunOnce entries cannot be disabled, only removed.");
        }

        return errors;
    }

    public override string Describe() => $"Startup '{Name}' ({Location}) {Ensure}";
}

/// <summary>State and default actions of one Windows Defender Firewall profile.</summary>
public sealed record FirewallProfileSetting(
    FirewallProfile Profile,
    bool? Enabled,
    FirewallAction? DefaultInboundAction,
    FirewallAction? DefaultOutboundAction) : ConfigurationSetting
{
    [JsonIgnore]
    public override SettingKind Kind => SettingKind.FirewallProfile;

    [JsonIgnore]
    public override string Key => $"firewall:profile:{Profile.ToString().ToLowerInvariant()}";

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(Profile))
        {
            errors.Add("profile must be Domain, Private or Public.");
        }

        if (Enabled is null && DefaultInboundAction is null && DefaultOutboundAction is null)
        {
            errors.Add("Specify at least one of enabled, defaultInboundAction, defaultOutboundAction.");
        }

        return errors;
    }

    public override string Describe() =>
        $"Firewall {Profile}" +
        (Enabled is { } e ? $" enabled={e}" : string.Empty) +
        (DefaultInboundAction is { } i ? $" inbound={i}" : string.Empty) +
        (DefaultOutboundAction is { } o ? $" outbound={o}" : string.Empty);
}

/// <summary>A Windows Defender Firewall rule owned by Solvix (grouped under "Solvix").</summary>
public sealed record FirewallRuleSetting(
    string Name,
    FirewallDirection Direction,
    FirewallAction Action,
    FirewallProtocol Protocol,
    string? LocalPorts,
    string? RemotePorts,
    string? RemoteAddresses,
    string? Program,
    FirewallProfiles Profiles = FirewallProfiles.All,
    bool Enabled = true,
    PresenceEnsure Ensure = PresenceEnsure.Present) : ConfigurationSetting
{
    public const string RuleGroup = "Solvix";

    private static readonly Regex PortsPattern = new(
        "^(\\*|((\\d{1,5})(-\\d{1,5})?)(,(\\d{1,5})(-\\d{1,5})?)*)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [JsonIgnore]
    public override SettingKind Kind => SettingKind.FirewallRule;

    [JsonIgnore]
    public override string Key => $"firewall:rule:{Lower(Name ?? string.Empty)}";

    [JsonIgnore]
    public bool HasAnyRemoteAddress => string.IsNullOrWhiteSpace(RemoteAddresses) || RemoteAddresses.Trim() == "*";

    [JsonIgnore]
    public bool HasAnyPort => string.IsNullOrWhiteSpace(LocalPorts) && string.IsNullOrWhiteSpace(RemotePorts);

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200 || Name.Contains('|', StringComparison.Ordinal))
        {
            errors.Add("name is required, at most 200 characters and cannot contain '|'.");
        }

        var portsAllowed = Protocol is FirewallProtocol.Tcp or FirewallProtocol.Udp;
        foreach (var (label, ports) in new[] { ("localPorts", LocalPorts), ("remotePorts", RemotePorts) })
        {
            if (string.IsNullOrWhiteSpace(ports))
            {
                continue;
            }

            if (!portsAllowed)
            {
                errors.Add($"{label} can only be set for Tcp or Udp rules.");
            }
            else if (!PortsPattern.IsMatch(ports.Replace(" ", string.Empty, StringComparison.Ordinal)) || !PortsInRange(ports))
            {
                errors.Add($"{label} must be '*' or a comma-separated list of ports/ranges between 1 and 65535.");
            }
        }

        if (Profiles == FirewallProfiles.None)
        {
            errors.Add("profiles cannot be None.");
        }

        if (Program is { Length: > 1024 })
        {
            errors.Add("program must be at most 1024 characters.");
        }

        if (RemoteAddresses is { Length: > 2048 })
        {
            errors.Add("remoteAddresses must be at most 2048 characters.");
        }

        return errors;
    }

    private static bool PortsInRange(string ports)
    {
        foreach (var token in ports.Replace(" ", string.Empty, StringComparison.Ordinal).Split(',', '-'))
        {
            if (token == "*")
            {
                continue;
            }

            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
            {
                return false;
            }
        }

        return true;
    }

    public override string Describe() =>
        Ensure == PresenceEnsure.Absent
            ? $"Remove firewall rule '{Name}'"
            : $"Firewall rule '{Name}' {Direction} {Action} {Protocol} {LocalPorts ?? "*"}";
}

/// <summary>A value of the active power plan (PowerWriteACValueIndex / PowerWriteDCValueIndex).</summary>
public sealed record PowerSetting(PowerSettingType Setting, PowerSource Source, int Value) : ConfigurationSetting
{
    public const int MaxTimeoutSeconds = 86_400 * 7;

    [JsonIgnore]
    public override SettingKind Kind => SettingKind.Power;

    [JsonIgnore]
    public override string Key => $"power:{Setting.ToString().ToLowerInvariant()}:{Source.ToString().ToLowerInvariant()}";

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Setting == PowerSettingType.LidCloseAction)
        {
            if (Value is < 0 or > 3)
            {
                errors.Add("LidCloseAction value must be 0 (do nothing), 1 (sleep), 2 (hibernate) or 3 (shut down).");
            }
        }
        else if (Value is < 0 or > MaxTimeoutSeconds)
        {
            errors.Add($"Timeout must be between 0 (never) and {MaxTimeoutSeconds} seconds.");
        }

        return errors;
    }

    public override string Describe() => $"Power {Setting} ({Source}) = {Value}";
}

/// <summary>Machine-level screen lock controls.</summary>
public sealed record ScreenLockSetting(ScreenLockControl Control, int Value) : ConfigurationSetting
{
    /// <summary>Upper bound documented for "Interactive logon: Machine inactivity limit".</summary>
    public const int MaxInactivitySeconds = 599_940;

    [JsonIgnore]
    public override SettingKind Kind => SettingKind.ScreenLock;

    [JsonIgnore]
    public override string Key => $"screenlock:{Control.ToString().ToLowerInvariant()}";

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        switch (Control)
        {
            case ScreenLockControl.InactivityTimeoutSeconds when Value is < 0 or > MaxInactivitySeconds:
                errors.Add($"InactivityTimeoutSeconds must be between 0 (disabled) and {MaxInactivitySeconds}.");
                break;
            case ScreenLockControl.RequirePasswordOnWake or ScreenLockControl.DisableLockWorkstation when Value is not (0 or 1):
                errors.Add($"{Control} value must be 0 or 1.");
                break;
            default:
                break;
        }

        return errors;
    }

    public override string Describe() => $"Screen lock {Control} = {Value}";
}

/// <summary>Removable storage access policy for one device class.</summary>
public sealed record UsbStorageSetting(UsbDeviceClass DeviceClass, UsbAccess Access) : ConfigurationSetting
{
    [JsonIgnore]
    public override SettingKind Kind => SettingKind.UsbStorage;

    [JsonIgnore]
    public override string Key => $"usb:{DeviceClass.ToString().ToLowerInvariant()}";

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (DeviceClass == UsbDeviceClass.AllRemovableStorage && Access == UsbAccess.ReadOnly)
        {
            errors.Add("AllRemovableStorage supports only FullAccess or NoAccess.");
        }

        return errors;
    }

    public override string Describe() => $"Removable storage {DeviceClass} = {Access}";
}
