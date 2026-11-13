namespace Solvix.UnitTests.EndpointManagement;

using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Configuration;

public sealed class SettingContractTests
{
    public static TheoryData<ConfigurationSetting, SettingRisk> RiskCases => new()
    {
        // Services: starting/enabling is hardening; stopping depends on what the service is.
        { new ServiceSetting("Spooler", ServiceStartupType.Automatic, ServiceRunState.Running), SettingRisk.Low },
        { new ServiceSetting("Spooler", ServiceStartupType.Disabled, null), SettingRisk.Elevated },
        { new ServiceSetting("WinDefend", null, ServiceRunState.Stopped), SettingRisk.Destructive },
        { new ServiceSetting("mpssvc", ServiceStartupType.Disabled, null), SettingRisk.Destructive },
        { new ServiceSetting("RpcSs", ServiceStartupType.Disabled, null), SettingRisk.Blocked },
        { new ServiceSetting("solvixagent", null, ServiceRunState.Stopped), SettingRisk.Blocked },

        // Registry: policies are low, generic writes elevated, sensitive/deletions destructive, credential stores blocked.
        { new RegistrySetting(@"SOFTWARE\Policies\Microsoft\Windows\Explorer", "NoAutoplayfornonVolume", RegistryValueType.DWord, "1"), SettingRisk.Low },
        { new RegistrySetting(@"SOFTWARE\Contoso\App", "Mode", RegistryValueType.String, "x"), SettingRisk.Elevated },
        { new RegistrySetting(@"SOFTWARE\Contoso\App", "Mode", RegistryValueType.String, null, PresenceEnsure.Absent), SettingRisk.Destructive },
        { new RegistrySetting(@"HKLM\SYSTEM\CurrentControlSet\Control\Lsa", "LmCompatibilityLevel", RegistryValueType.DWord, "5"), SettingRisk.Destructive },
        { new RegistrySetting(@"SECURITY\Policy", "x", RegistryValueType.DWord, "1"), SettingRisk.Blocked },
        { new RegistrySetting(@"SYSTEM\CurrentControlSet\Services\SolvixAgent", "Start", RegistryValueType.DWord, "4"), SettingRisk.Blocked },

        // Startup apps.
        { new StartupAppSetting("OneDrive", StartupLocation.MachineRun, null, StartupEnsure.Disabled), SettingRisk.Low },
        { new StartupAppSetting("Tool", StartupLocation.MachineRun, "c:\\tool.exe", StartupEnsure.Present), SettingRisk.Elevated },
        { new StartupAppSetting("Tool", StartupLocation.MachineRun, null, StartupEnsure.Absent), SettingRisk.Destructive },

        // Firewall.
        { new FirewallProfileSetting(FirewallProfile.Public, true, FirewallAction.Block, FirewallAction.Allow), SettingRisk.Low },
        { new FirewallProfileSetting(FirewallProfile.Public, false, null, null), SettingRisk.Destructive },
        { new FirewallProfileSetting(FirewallProfile.Domain, null, FirewallAction.Allow, null), SettingRisk.Destructive },
        { new FirewallProfileSetting(FirewallProfile.Domain, null, null, FirewallAction.Block), SettingRisk.Destructive },
        { new FirewallRuleSetting("Block SMB", FirewallDirection.Inbound, FirewallAction.Block, FirewallProtocol.Tcp, "445", null, null, null), SettingRisk.Low },
        { new FirewallRuleSetting("Allow RDP", FirewallDirection.Inbound, FirewallAction.Allow, FirewallProtocol.Tcp, "3389", null, "*", null), SettingRisk.Elevated },
        { new FirewallRuleSetting("Allow RDP from VPN", FirewallDirection.Inbound, FirewallAction.Allow, FirewallProtocol.Tcp, "3389", null, "10.8.0.0/16", null), SettingRisk.Low },
        { new FirewallRuleSetting("Block all out", FirewallDirection.Outbound, FirewallAction.Block, FirewallProtocol.Any, null, null, null, null), SettingRisk.Destructive },

        // Power, screen lock, USB.
        { new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Ac, 1800), SettingRisk.Low },
        { new ScreenLockSetting(ScreenLockControl.InactivityTimeoutSeconds, 900), SettingRisk.Low },
        { new ScreenLockSetting(ScreenLockControl.InactivityTimeoutSeconds, 0), SettingRisk.Elevated },
        { new ScreenLockSetting(ScreenLockControl.RequirePasswordOnWake, 0), SettingRisk.Elevated },
        { new ScreenLockSetting(ScreenLockControl.RequireCtrlAltDel, 0), SettingRisk.Elevated },
        { new UsbStorageSetting(UsbDeviceClass.RemovableDisks, UsbAccess.NoAccess), SettingRisk.Low },
    };

    [Theory]
    [MemberData(nameof(RiskCases))]
    public void Classifies_risk(ConfigurationSetting setting, SettingRisk expected) =>
        Assert.Equal(expected, SettingRiskClassifier.Classify(setting));

    [Fact]
    public void Max_risk_of_a_set_is_the_highest_member()
    {
        Assert.Equal(SettingRisk.Low, SettingRiskClassifier.Max([]));
        Assert.Equal(
            SettingRisk.Destructive,
            SettingRiskClassifier.Max([new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Ac, 0), new FirewallProfileSetting(FirewallProfile.Public, false, null, null)]));
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Contoso", @"registry:hklm\software\contoso\mode")]
    [InlineData(@"HKEY_LOCAL_MACHINE\Software\Contoso\", @"registry:hklm\software\contoso\mode")]
    [InlineData(@"software//contoso", @"registry:hklm\software\contoso\mode")]
    public void Registry_keys_are_normalised_so_equivalent_paths_conflict(string path, string expectedKey) =>
        Assert.Equal(expectedKey, new RegistrySetting(path, "Mode", RegistryValueType.String, "x").Key);

    [Fact]
    public void Keys_are_case_insensitive_targets()
    {
        Assert.Equal(new ServiceSetting("Spooler", ServiceStartupType.Disabled, null).Key, new ServiceSetting("SPOOLER", ServiceStartupType.Manual, null).Key);
        Assert.NotEqual(new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Ac, 1).Key, new PowerSetting(PowerSettingType.SleepTimeout, PowerSource.Dc, 1).Key);
    }

    [Theory]
    [InlineData(RegistryValueType.DWord, "0x10", true, "16")]
    [InlineData(RegistryValueType.DWord, "4294967296", false, "")]
    [InlineData(RegistryValueType.DWord, "-1", false, "")]
    [InlineData(RegistryValueType.QWord, "18446744073709551615", true, "18446744073709551615")]
    [InlineData(RegistryValueType.Binary, "de ad,BE ef", true, "DEADBEEF")]
    [InlineData(RegistryValueType.Binary, "abc", false, "")]
    [InlineData(RegistryValueType.MultiString, "a\r\nb", true, "a\nb")]
    public void Registry_values_are_validated_and_canonicalised(RegistryValueType type, string input, bool valid, string expected)
    {
        Assert.Equal(valid, RegistrySetting.TryNormalizeValue(type, input, out var normalized));
        if (valid)
        {
            Assert.Equal(expected, normalized);
        }
    }

    [Fact]
    public void Validation_rejects_inconsistent_settings()
    {
        Assert.NotEmpty(new ServiceSetting("Spooler", ServiceStartupType.Disabled, ServiceRunState.Running).Validate());
        Assert.NotEmpty(new ServiceSetting("bad name!", ServiceStartupType.Manual, null).Validate());
        Assert.NotEmpty(new ServiceSetting("Spooler", null, null).Validate());
        Assert.NotEmpty(new RegistrySetting(@"SOFTWARE\..\SAM", "x", RegistryValueType.DWord, "1").Validate());
        Assert.NotEmpty(new RegistrySetting(@"SOFTWARE\X", "x", RegistryValueType.DWord, "abc").Validate());
        Assert.NotEmpty(new StartupAppSetting("App", StartupLocation.MachineRun, null, StartupEnsure.Present).Validate());
        Assert.NotEmpty(new StartupAppSetting("App", StartupLocation.MachineRunOnce, null, StartupEnsure.Disabled).Validate());
        Assert.NotEmpty(new FirewallRuleSetting("Icmp", FirewallDirection.Inbound, FirewallAction.Allow, FirewallProtocol.Icmpv4, "80", null, null, null).Validate());
        Assert.NotEmpty(new FirewallRuleSetting("Web", FirewallDirection.Inbound, FirewallAction.Allow, FirewallProtocol.Tcp, "70000", null, null, null).Validate());
        Assert.Empty(new FirewallRuleSetting("Web", FirewallDirection.Inbound, FirewallAction.Allow, FirewallProtocol.Tcp, "80,443,8000-8100", null, null, null).Validate());
        Assert.NotEmpty(new PowerSetting(PowerSettingType.LidCloseAction, PowerSource.Dc, 7).Validate());
        Assert.NotEmpty(new ScreenLockSetting(ScreenLockControl.InactivityTimeoutSeconds, 600_000).Validate());
        Assert.NotEmpty(new UsbStorageSetting(UsbDeviceClass.AllRemovableStorage, UsbAccess.ReadOnly).Validate());
    }

    [Fact]
    public void Polymorphic_json_round_trips_and_hash_is_stable()
    {
        ConfigurationSetting original = new FirewallRuleSetting("Web", FirewallDirection.Inbound, FirewallAction.Allow, FirewallProtocol.Tcp, "443", null, "10.0.0.0/8", null);
        var json = ContractJson.SerializeSetting(original);
        Assert.Contains("\"kind\":\"FirewallRule\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hasAnyPort", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"key\"", json, StringComparison.Ordinal);

        var back = ContractJson.DeserializeSetting(json);
        Assert.Equal(original, back);
        Assert.Equal(ContractJson.Hash(original), ContractJson.Hash(back));
        Assert.NotEqual(ContractJson.Hash(original), ContractJson.Hash(((FirewallRuleSetting)original) with { LocalPorts = "8443" }));
    }
}
