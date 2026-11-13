namespace Solvix.UnitTests.Agent;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Solvix.Agent.Core.Configuration;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Configuration;

/// <summary>An in-memory "device" for screen lock and service settings.</summary>
internal sealed class FakeProvider(SettingKind kind) : ISettingProvider
{
    public Dictionary<string, string?> State { get; } = new(StringComparer.Ordinal);

    public bool Rollback { get; set; } = true;

    public bool Sticky { get; set; } = true;

    public bool ThrowOnApply { get; set; }

    public int Applies { get; private set; }

    public SettingKind Kind => kind;

    public bool SupportsRollback => Rollback;

    public static string Value(ConfigurationSetting s) => s switch
    {
        ScreenLockSetting l => l.Value.ToString(CultureInfo.InvariantCulture),
        ServiceSetting svc => svc.StartupType?.ToString() ?? string.Empty,
        _ => s.Describe(),
    };

    public Observation Evaluate(ConfigurationSetting desired)
    {
        if (desired is ServiceSetting && !State.ContainsKey(desired.Key))
        {
            return new Observation(false, false, Value(desired), "(missing)");
        }

        var actual = State.GetValueOrDefault(desired.Key);
        return new Observation(true, actual == Value(desired), Value(desired), actual ?? "(unset)");
    }

    public void Apply(ConfigurationSetting desired)
    {
        Applies++;
        if (ThrowOnApply)
        {
            throw new UnauthorizedAccessException("Access is denied.");
        }

        if (Sticky)
        {
            State[desired.Key] = Value(desired);
        }
    }

    public string Capture(ConfigurationSetting desired) => State.GetValueOrDefault(desired.Key) ?? "<unset>";

    public void Restore(ConfigurationSetting desired, string capturedState) =>
        State[desired.Key] = capturedState == "<unset>" ? null : capturedState;
}

public sealed class ConfigurationEvaluatorTests
{
    private static readonly ScreenLockSetting Lock900 = new(ScreenLockControl.InactivityTimeoutSeconds, 900);

    private readonly FakeProvider _locks = new(SettingKind.ScreenLock);
    private readonly FakeProvider _services = new(SettingKind.Service);
    private readonly InMemorySnapshotStore _snapshots = new();
    private readonly InMemoryAppliedSettingsLedger _ledger = new();

    private ConfigurationEvaluator Evaluator(SettingRisk max = SettingRisk.Destructive) =>
        new([_locks, _services], _snapshots, _ledger, TimeProvider.System, NullLogger<ConfigurationEvaluator>.Instance) { MaxAllowedRisk = max };

    private static DesiredSetting Desired(ConfigurationSetting s, EnforcementMode mode = EnforcementMode.Enforce, bool applyAllowed = true, SettingRisk? authorized = null) =>
        new(s.Key, s, authorized ?? SettingRiskClassifier.Classify(s), mode, applyAllowed, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), ContractJson.Hash(s));

    private static ConfigurationDocument Doc(IReadOnlyList<DesiredSetting> settings, IReadOnlyList<string>? revert = null) =>
        new("rev", DateTimeOffset.UtcNow, settings, revert ?? [], []);

    private SettingResult Run(DesiredSetting desired, SettingRisk max = SettingRisk.Destructive) =>
        Assert.Single(Evaluator(max).Evaluate(Doc([desired])).Results);

    [Fact]
    public void Applies_snapshots_and_then_reports_compliance()
    {
        _locks.State[Lock900.Key] = "0";
        var first = Run(Desired(Lock900));
        Assert.Equal(SettingComplianceStatus.Applied, first.Status);
        Assert.Equal("900", _locks.State[Lock900.Key]);
        Assert.Equal("0", _snapshots.Get(Lock900.Key)!.State);
        Assert.True(first.RollbackAvailable);

        Assert.Equal(SettingComplianceStatus.Compliant, Run(Desired(Lock900)).Status);
    }

    [Fact]
    public void Drift_after_application_is_remediated_and_the_original_snapshot_is_kept()
    {
        _locks.State[Lock900.Key] = "0";
        Run(Desired(Lock900));
        _locks.State[Lock900.Key] = "60"; // a local admin changed it

        var result = Run(Desired(Lock900));
        Assert.Equal(SettingComplianceStatus.Remediated, result.Status);
        Assert.Equal("900", _locks.State[Lock900.Key]);
        Assert.Equal("0", _snapshots.Get(Lock900.Key)!.State);
    }

    [Fact]
    public void Monitor_mode_and_closed_windows_only_report_drift()
    {
        _locks.State[Lock900.Key] = "60";
        Assert.Equal(SettingComplianceStatus.Drifted, Run(Desired(Lock900, EnforcementMode.Monitor)).Status);
        var waiting = Run(Desired(Lock900, applyAllowed: false));
        Assert.Equal(SettingComplianceStatus.Drifted, waiting.Status);
        Assert.Contains("maintenance window", waiting.Message, StringComparison.Ordinal);
        Assert.Equal(0, _locks.Applies);
        Assert.Null(_snapshots.Get(Lock900.Key));
    }

    [Fact]
    public void Settings_riskier_than_authorised_are_refused_without_touching_the_device()
    {
        var disable = new ServiceSetting("Spooler", ServiceStartupType.Disabled, null);
        _services.State[disable.Key] = "Automatic";

        // The server claims only Low risk was approved; the agent computes Elevated itself.
        var result = Run(Desired(disable, authorized: SettingRisk.Low));
        Assert.Equal(SettingComplianceStatus.Refused, result.Status);
        Assert.Equal(0, _services.Applies);
        Assert.Equal("Automatic", _services.State[disable.Key]);
    }

    [Fact]
    public void Blocked_settings_are_refused_even_if_the_server_authorised_them()
    {
        var rpc = new ServiceSetting("RpcSs", ServiceStartupType.Disabled, null);
        _services.State[rpc.Key] = "Automatic";
        Assert.Equal(SettingComplianceStatus.Refused, Run(Desired(rpc, authorized: SettingRisk.Blocked)).Status);
        Assert.Equal(0, _services.Applies);
    }

    [Fact]
    public void Local_risk_ceiling_overrides_server_authorisation()
    {
        var disable = new ServiceSetting("Spooler", ServiceStartupType.Disabled, null);
        _services.State[disable.Key] = "Automatic";
        Assert.Equal(SettingComplianceStatus.Refused, Run(Desired(disable), max: SettingRisk.Low).Status);
        Assert.Equal(SettingComplianceStatus.Applied, Run(Desired(disable), max: SettingRisk.Elevated).Status);
    }

    [Fact]
    public void Tampered_setting_that_does_not_match_its_hash_is_refused()
    {
        var desired = Desired(Lock900) with { Setting = new ScreenLockSetting(ScreenLockControl.InactivityTimeoutSeconds, 0) };
        Assert.Equal(SettingComplianceStatus.Refused, Run(desired).Status);
    }

    [Fact]
    public void Failures_are_reported_not_thrown()
    {
        _locks.State[Lock900.Key] = "0";
        _locks.ThrowOnApply = true;
        var failed = Run(Desired(Lock900));
        Assert.Equal(SettingComplianceStatus.Failed, failed.Status);
        Assert.Contains("Access is denied", failed.Message, StringComparison.Ordinal);

        _locks.ThrowOnApply = false;
        _locks.Sticky = false; // e.g. overwritten by domain Group Policy
        Assert.Equal(SettingComplianceStatus.Failed, Run(Desired(Lock900)).Status);
    }

    [Fact]
    public void Missing_targets_are_not_applicable()
    {
        var svc = new ServiceSetting("NotInstalled", ServiceStartupType.Disabled, null);
        Assert.Equal(SettingComplianceStatus.NotApplicable, Run(Desired(svc)).Status);
    }

    [Fact]
    public void Revert_restores_the_original_value_once()
    {
        _locks.State[Lock900.Key] = "0";
        Run(Desired(Lock900));

        var reverted = Assert.Single(Evaluator().Evaluate(Doc([], [Lock900.Key])).Results);
        Assert.Equal(SettingComplianceStatus.Reverted, reverted.Status);
        Assert.Equal("0", _locks.State[Lock900.Key]);
        Assert.Null(_snapshots.Get(Lock900.Key));

        // A repeated request (e.g. lost report) is answered without changing anything.
        Assert.Equal(SettingComplianceStatus.RevertUnsupported, Assert.Single(Evaluator().Evaluate(Doc([], [Lock900.Key])).Results).Status);
    }

    [Fact]
    public void Revert_of_a_value_that_did_not_exist_removes_it_again()
    {
        Run(Desired(Lock900));
        Assert.Equal(SettingComplianceStatus.Reverted, Assert.Single(Evaluator().Evaluate(Doc([], [Lock900.Key])).Results).Status);
        Assert.Null(_locks.State[Lock900.Key]);
    }

    [Fact]
    public void Providers_without_rollback_report_revert_unsupported()
    {
        _locks.Rollback = false;
        _locks.State[Lock900.Key] = "0";
        var applied = Run(Desired(Lock900));
        Assert.Equal(SettingComplianceStatus.Applied, applied.Status);
        Assert.False(applied.RollbackAvailable);
        Assert.Equal(SettingComplianceStatus.RevertUnsupported, Assert.Single(Evaluator().Evaluate(Doc([], [Lock900.Key])).Results).Status);
    }
}
