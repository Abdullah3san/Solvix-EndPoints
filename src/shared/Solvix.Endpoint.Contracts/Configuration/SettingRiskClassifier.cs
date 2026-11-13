namespace Solvix.Endpoint.Contracts.Configuration;

/// <summary>
/// Single source of truth for how dangerous a setting is. The server uses it to
/// decide who may approve a policy version; the agent re-runs it before touching
/// the device and refuses anything riskier than what the server authorised, so a
/// tampered or buggy server response cannot escalate a change on its own.
/// </summary>
public static class SettingRiskClassifier
{
    /// <summary>Name of the Solvix agent Windows service. The agent never lets a policy disable itself.</summary>
    public const string AgentServiceName = "SolvixAgent";

    /// <summary>Services whose loss makes Windows unusable or unmanageable.</summary>
    private static readonly HashSet<string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
    {
        AgentServiceName,
        "RpcSs",
        "RpcEptMapper",
        "DcomLaunch",
        "LSM",
        "SamSs",
        "Winmgmt",
        "PlugPlay",
        "Power",
        "ProfSvc",
        "BrokerInfrastructure",
        "CoreMessagingRegistrar",
        "gpsvc",
        "SystemEventsBroker",
        "Schedule",
        "TrustedInstaller",
    };

    /// <summary>Security and update services; turning them off weakens the device.</summary>
    private static readonly HashSet<string> SecurityServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend",
        "WdNisSvc",
        "Sense",
        "SecurityHealthService",
        "wscsvc",
        "mpssvc",
        "BFE",
        "EventLog",
        "wuauserv",
        "UsoSvc",
        "BITS",
        "CryptSvc",
        "Dnscache",
        "Dhcp",
        "nsi",
        "LanmanWorkstation",
        "W32Time",
        "Netlogon",
    };

    /// <summary>Registry roots that may never be written (credential stores, boot configuration).</summary>
    private static readonly string[] BlockedRegistryPrefixes =
    [
        "sam",
        "security",
        "bcd00000000",
        "hardware",
        "system\\currentcontrolset\\control\\lsa\\data",
        "system\\currentcontrolset\\control\\lsa\\jd",
        "system\\currentcontrolset\\control\\lsa\\skew1",
        "system\\currentcontrolset\\control\\lsa\\gbg",
        "system\\currentcontrolset\\services\\" + AgentServiceName.ToLowerInvariant(),
    ];

    /// <summary>Registry roots where a wrong value can prevent boot or sign-in, or disable security subsystems.</summary>
    private static readonly string[] SensitiveRegistryPrefixes =
    [
        "system\\currentcontrolset\\services",
        "system\\currentcontrolset\\control\\lsa",
        "system\\currentcontrolset\\control\\securityproviders",
        "system\\currentcontrolset\\control\\session manager",
        "system\\currentcontrolset\\control\\safeboot",
        "system\\setup",
        "software\\microsoft\\windows nt\\currentversion\\winlogon",
        "software\\microsoft\\windows nt\\currentversion\\image file execution options",
        "software\\microsoft\\windows defender",
        "software\\policies\\microsoft\\windows defender",
        "software\\microsoft\\windows\\currentversion\\policies\\system",
        "software\\policies\\microsoft\\windowsfirewall",
        "system\\currentcontrolset\\services\\sharedaccess",
    ];

    /// <summary>Policy branch: settings here are designed to be managed and are reversible.</summary>
    private const string PoliciesPrefix = "software\\policies";

    public static SettingRisk Classify(ConfigurationSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return setting switch
        {
            ServiceSetting s => ClassifyService(s),
            RegistrySetting r => ClassifyRegistry(r),
            StartupAppSetting a => a.Ensure switch
            {
                StartupEnsure.Absent => SettingRisk.Destructive,
                StartupEnsure.Present => SettingRisk.Elevated,
                _ => SettingRisk.Low,
            },
            FirewallProfileSetting f => ClassifyFirewallProfile(f),
            FirewallRuleSetting r => ClassifyFirewallRule(r),
            PowerSetting => SettingRisk.Low,
            ScreenLockSetting l => ClassifyScreenLock(l),
            UsbStorageSetting => SettingRisk.Low,
            _ => SettingRisk.Blocked,
        };
    }

    /// <summary>Highest risk of a set of settings (<see cref="SettingRisk.Low"/> for none).</summary>
    public static SettingRisk Max(IEnumerable<ConfigurationSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var max = SettingRisk.Low;
        foreach (var s in settings)
        {
            var r = Classify(s);
            if (r > max)
            {
                max = r;
            }
        }

        return max;
    }

    /// <summary>Explains why a setting has its risk; shown to approvers.</summary>
    public static string Explain(ConfigurationSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return (setting, Classify(setting)) switch
        {
            (_, SettingRisk.Low) => "Hardening or reversible change.",
            (ServiceSetting s, SettingRisk.Blocked) => $"'{s.ServiceName}' is required by Windows or by the Solvix agent and cannot be stopped or disabled.",
            (ServiceSetting s, SettingRisk.Destructive) => $"Stopping or disabling '{s.ServiceName}' turns off a security or connectivity component.",
            (ServiceSetting, _) => "Stopping or disabling a service can interrupt users or applications.",
            (RegistrySetting, SettingRisk.Blocked) => "This registry location holds credentials, boot data or the agent's own configuration.",
            (RegistrySetting { Ensure: PresenceEnsure.Absent }, _) => "Deletes a registry value.",
            (RegistrySetting, SettingRisk.Destructive) => "Writes to a registry location that controls boot, sign-in or security subsystems.",
            (RegistrySetting, _) => "Writes an arbitrary machine registry value.",
            (StartupAppSetting { Ensure: StartupEnsure.Absent }, _) => "Deletes a startup entry.",
            (StartupAppSetting, _) => "Adds a program that runs at every sign-in.",
            (FirewallProfileSetting, _) => "Turns off the firewall, allows unsolicited inbound traffic or blocks outbound traffic (can cut the management connection).",
            (FirewallRuleSetting { Ensure: PresenceEnsure.Absent }, _) => "Removes a firewall rule.",
            (FirewallRuleSetting { Direction: FirewallDirection.Outbound }, _) => "Blocks all outbound traffic for the matched scope (can cut the management connection).",
            (FirewallRuleSetting, _) => "Opens inbound access from any address.",
            (ScreenLockSetting, _) => "Weakens or disables screen locking.",
            _ => "Elevated risk.",
        };
    }

    private static SettingRisk ClassifyService(ServiceSetting s)
    {
        var turnsOff = s.StartupType == ServiceStartupType.Disabled || s.State == ServiceRunState.Stopped;
        if (!turnsOff)
        {
            return SettingRisk.Low;
        }

        if (CriticalServices.Contains(s.ServiceName))
        {
            return SettingRisk.Blocked;
        }

        return SecurityServices.Contains(s.ServiceName) ? SettingRisk.Destructive : SettingRisk.Elevated;
    }

    private static SettingRisk ClassifyRegistry(RegistrySetting r)
    {
        var path = RegistrySetting.NormalizePath(r.Path);
        if (StartsWithAny(path, BlockedRegistryPrefixes))
        {
            return SettingRisk.Blocked;
        }

        if (r.Ensure == PresenceEnsure.Absent || StartsWithAny(path, SensitiveRegistryPrefixes))
        {
            return SettingRisk.Destructive;
        }

        return path == PoliciesPrefix || path.StartsWith(PoliciesPrefix + "\\", StringComparison.Ordinal)
            ? SettingRisk.Low
            : SettingRisk.Elevated;
    }

    private static SettingRisk ClassifyFirewallProfile(FirewallProfileSetting f) =>
        f.Enabled == false || f.DefaultInboundAction == FirewallAction.Allow || f.DefaultOutboundAction == FirewallAction.Block
            ? SettingRisk.Destructive
            : SettingRisk.Low;

    private static SettingRisk ClassifyFirewallRule(FirewallRuleSetting r)
    {
        if (r.Ensure == PresenceEnsure.Absent)
        {
            return SettingRisk.Elevated;
        }

        if (!r.Enabled)
        {
            return SettingRisk.Low;
        }

        if (r.Direction == FirewallDirection.Outbound && r.Action == FirewallAction.Block && r.HasAnyRemoteAddress && r.HasAnyPort && string.IsNullOrWhiteSpace(r.Program))
        {
            return SettingRisk.Destructive;
        }

        return r.Direction == FirewallDirection.Inbound && r.Action == FirewallAction.Allow && r.HasAnyRemoteAddress
            ? SettingRisk.Elevated
            : SettingRisk.Low;
    }

    private static SettingRisk ClassifyScreenLock(ScreenLockSetting l) => l.Control switch
    {
        ScreenLockControl.InactivityTimeoutSeconds when l.Value == 0 => SettingRisk.Elevated,
        ScreenLockControl.RequirePasswordOnWake when l.Value == 0 => SettingRisk.Elevated,
        ScreenLockControl.DisableLockWorkstation when l.Value == 1 => SettingRisk.Elevated,
        _ => SettingRisk.Low,
    };

    private static bool StartsWithAny(string path, IEnumerable<string> prefixes) =>
        prefixes.Any(p => path == p || path.StartsWith(p + "\\", StringComparison.Ordinal));
}
