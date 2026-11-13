namespace Solvix.Agent.Windows.Providers;

using System.Runtime.InteropServices;
using Solvix.Agent.Core.Configuration;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Configuration;

/// <summary>Windows Defender Firewall API (INetFwPolicy2 / INetFwRule) through COM late binding.</summary>
internal static class FirewallApi
{
    /// <summary>NET_FW_PROFILE2_ALL.</summary>
    public const int AllProfiles = 0x7FFFFFFF;

    public static dynamic Policy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    public static dynamic NewRule() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;

    public static dynamic? FindRule(dynamic policy, string name)
    {
        foreach (var rule in policy.Rules)
        {
            if (string.Equals((string)rule.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>INetFwRules.Remove removes one rule per call; repeat for duplicates.</summary>
    public static void RemoveAll(dynamic policy, string name)
    {
        for (var i = 0; i < 100 && FindRule(policy, name) is not null; i++)
        {
            policy.Rules.Remove(name);
        }
    }
}

internal sealed class FirewallProfileSettingProvider : ISettingProvider
{
    public SettingKind Kind => SettingKind.FirewallProfile;

    public bool SupportsRollback => true;

    private sealed record ProfileState(bool Enabled, int Inbound, int Outbound);

    public Observation Evaluate(ConfigurationSetting desired)
    {
        var f = (FirewallProfileSetting)desired;
        var actual = Read((int)f.Profile);
        var compliant = (f.Enabled is null || f.Enabled == actual.Enabled)
            && (f.DefaultInboundAction is null || (int)f.DefaultInboundAction == actual.Inbound)
            && (f.DefaultOutboundAction is null || (int)f.DefaultOutboundAction == actual.Outbound);
        return new Observation(true, compliant, f.Describe(), Describe(actual));
    }

    public void Apply(ConfigurationSetting desired)
    {
        var f = (FirewallProfileSetting)desired;
        var policy = FirewallApi.Policy();
        try
        {
            var profile = (int)f.Profile;
            if (f.DefaultInboundAction is { } inbound)
            {
                policy.DefaultInboundAction[profile] = (int)inbound;
            }

            if (f.DefaultOutboundAction is { } outbound)
            {
                policy.DefaultOutboundAction[profile] = (int)outbound;
            }

            if (f.Enabled is { } enabled)
            {
                policy.FirewallEnabled[profile] = enabled;
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(policy);
        }
    }

    public string Capture(ConfigurationSetting desired) =>
        ContractJson.Serialize(Read((int)((FirewallProfileSetting)desired).Profile));

    public void Restore(ConfigurationSetting desired, string capturedState)
    {
        var f = (FirewallProfileSetting)desired;
        var state = ContractJson.Deserialize<ProfileState>(capturedState)!;
        Apply(new FirewallProfileSetting(f.Profile, state.Enabled, (FirewallAction)state.Inbound, (FirewallAction)state.Outbound));
    }

    private static ProfileState Read(int profile)
    {
        var policy = FirewallApi.Policy();
        try
        {
            return new ProfileState((bool)policy.FirewallEnabled[profile], (int)policy.DefaultInboundAction[profile], (int)policy.DefaultOutboundAction[profile]);
        }
        finally
        {
            Marshal.FinalReleaseComObject(policy);
        }
    }

    private static string Describe(ProfileState s) =>
        $"enabled={s.Enabled} inbound={(FirewallAction)s.Inbound} outbound={(FirewallAction)s.Outbound}";
}

internal sealed class FirewallRuleSettingProvider : ISettingProvider
{
    public SettingKind Kind => SettingKind.FirewallRule;

    public bool SupportsRollback => true;

    private sealed record RuleState(
        bool Exists,
        int Direction,
        int Action,
        int Protocol,
        string? LocalPorts,
        string? RemotePorts,
        string? RemoteAddresses,
        string? Program,
        int Profiles,
        bool Enabled,
        string? Grouping,
        string? Description);

    public Observation Evaluate(ConfigurationSetting desired)
    {
        var r = (FirewallRuleSetting)desired;
        var actual = Read(r.Name);
        if (r.Ensure == PresenceEnsure.Absent)
        {
            return new Observation(true, !actual.Exists, "(absent)", actual.Exists ? "present" : "(absent)");
        }

        var wanted = ToState(r);
        var compliant = actual.Exists
            && actual.Direction == wanted.Direction
            && actual.Action == wanted.Action
            && actual.Protocol == wanted.Protocol
            && Same(actual.LocalPorts, wanted.LocalPorts)
            && Same(actual.RemotePorts, wanted.RemotePorts)
            && Same(actual.RemoteAddresses, wanted.RemoteAddresses)
            && Same(actual.Program, wanted.Program)
            && actual.Profiles == wanted.Profiles
            && actual.Enabled == wanted.Enabled;
        return new Observation(true, compliant, r.Describe(), actual.Exists ? $"{(FirewallDirection)actual.Direction} {(FirewallAction)actual.Action} proto={actual.Protocol} ports={actual.LocalPorts} enabled={actual.Enabled}" : "(absent)");
    }

    public void Apply(ConfigurationSetting desired)
    {
        var r = (FirewallRuleSetting)desired;
        Write(r.Name, r.Ensure == PresenceEnsure.Absent ? null : ToState(r));
    }

    public string Capture(ConfigurationSetting desired) =>
        ContractJson.Serialize(Read(((FirewallRuleSetting)desired).Name));

    public void Restore(ConfigurationSetting desired, string capturedState)
    {
        var state = ContractJson.Deserialize<RuleState>(capturedState)!;
        Write(((FirewallRuleSetting)desired).Name, state.Exists ? state : null);
    }

    private static RuleState ToState(FirewallRuleSetting r) => new(
        true,
        (int)r.Direction,
        (int)r.Action,
        (int)r.Protocol,
        Normalize(r.LocalPorts),
        Normalize(r.RemotePorts),
        Normalize(r.RemoteAddresses),
        string.IsNullOrWhiteSpace(r.Program) ? null : r.Program,
        r.Profiles == FirewallProfiles.All ? FirewallApi.AllProfiles : (int)r.Profiles,
        r.Enabled,
        FirewallRuleSetting.RuleGroup,
        "Managed by Solvix");

    private static RuleState Read(string name)
    {
        var policy = FirewallApi.Policy();
        try
        {
            var rule = FirewallApi.FindRule(policy, name);
            if (rule is null)
            {
                return new RuleState(false, 0, 0, 0, null, null, null, null, 0, false, null, null);
            }

            int protocol = rule.Protocol;
            var portsAllowed = protocol is 6 or 17;
            return new RuleState(
                true,
                (int)rule.Direction,
                (int)rule.Action,
                protocol,
                portsAllowed ? Normalize((string?)rule.LocalPorts) : null,
                portsAllowed ? Normalize((string?)rule.RemotePorts) : null,
                Normalize((string?)rule.RemoteAddresses),
                (string?)rule.ApplicationName,
                (int)rule.Profiles,
                (bool)rule.Enabled,
                (string?)rule.Grouping,
                (string?)rule.Description);
        }
        finally
        {
            Marshal.FinalReleaseComObject(policy);
        }
    }

    private static void Write(string name, RuleState? state)
    {
        var policy = FirewallApi.Policy();
        try
        {
            FirewallApi.RemoveAll(policy, name);
            if (state is null)
            {
                return;
            }

            var rule = FirewallApi.NewRule();
            rule.Name = name;
            rule.Description = state.Description ?? "Managed by Solvix";
            rule.Grouping = state.Grouping ?? FirewallRuleSetting.RuleGroup;
            rule.Direction = state.Direction;
            rule.Action = state.Action;
            rule.Protocol = state.Protocol;
            if (state.Protocol is 6 or 17)
            {
                rule.LocalPorts = state.LocalPorts ?? "*";
                rule.RemotePorts = state.RemotePorts ?? "*";
            }

            rule.RemoteAddresses = state.RemoteAddresses ?? "*";
            if (!string.IsNullOrWhiteSpace(state.Program))
            {
                rule.ApplicationName = state.Program;
            }

            rule.Profiles = state.Profiles;
            rule.Enabled = state.Enabled;
            policy.Rules.Add(rule);
        }
        finally
        {
            Marshal.FinalReleaseComObject(policy);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == "*" ? "*" : value.Replace(" ", string.Empty, StringComparison.Ordinal);

    private static bool Same(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
}
