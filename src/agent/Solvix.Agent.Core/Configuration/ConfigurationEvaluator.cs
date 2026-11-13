namespace Solvix.Agent.Core.Configuration;

using Microsoft.Extensions.Logging;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Configuration;

/// <summary>Remembers which value hash the agent last applied for each key (Applied vs Remediated).</summary>
public interface IAppliedSettingsLedger
{
    string? GetAppliedHash(string key);

    void SetAppliedHash(string key, string? hash);
}

public sealed class InMemoryAppliedSettingsLedger : IAppliedSettingsLedger
{
    private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);

    public string? GetAppliedHash(string key) => _hashes.GetValueOrDefault(key);

    public void SetAppliedHash(string key, string? hash)
    {
        if (hash is null)
        {
            _hashes.Remove(key);
        }
        else
        {
            _hashes[key] = hash;
        }
    }
}

/// <summary>
/// Evaluates a <see cref="ConfigurationDocument"/> against the device and returns
/// one result per setting. Safety rules enforced locally, independent of the server:
/// <list type="bullet">
/// <item>the agent re-classifies every setting and <b>refuses</b> anything Blocked or riskier than the
///   risk the policy's approvers authorised (<see cref="DesiredSetting.AuthorizedRisk"/>) or than the
///   local cap (<see cref="MaxAllowedRisk"/>);</item>
/// <item>Monitor mode, and Enforce mode outside the maintenance window, never change the device;</item>
/// <item>before the first change to a key the original state is snapshotted, so the change can be
///   reverted later (where the provider supports it);</item>
/// <item>every change is verified by re-reading the device.</item>
/// </list>
/// </summary>
public sealed partial class ConfigurationEvaluator(
    IEnumerable<ISettingProvider> providers,
    ISnapshotStore snapshots,
    IAppliedSettingsLedger ledger,
    TimeProvider time,
    ILogger<ConfigurationEvaluator> logger)
{
    private readonly Dictionary<SettingKind, ISettingProvider> _providers = providers.ToDictionary(p => p.Kind);

    /// <summary>Local ceiling on what the agent will apply, configurable by the device owner (default: whatever was authorised).</summary>
    public SettingRisk MaxAllowedRisk { get; init; } = SettingRisk.Destructive;

    public ConfigurationReport Evaluate(ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var results = new List<SettingResult>();
        foreach (var desired in document.Settings)
        {
            results.Add(EvaluateOne(desired));
        }

        foreach (var key in document.RevertKeys)
        {
            results.Add(Revert(key));
        }

        return new ConfigurationReport(document.Revision, results);
    }

    private SettingResult EvaluateOne(DesiredSetting desired)
    {
        var setting = desired.Setting;
        var key = setting.Key;
        if (!string.Equals(key, desired.Key, StringComparison.Ordinal) || !string.Equals(ContractJson.Hash(setting), desired.Hash, StringComparison.Ordinal))
        {
            return Result(desired, SettingComplianceStatus.Refused, null, null, "The setting does not match its key or hash; ignored.", false);
        }

        var risk = SettingRiskClassifier.Classify(setting);
        if (risk == SettingRisk.Blocked || risk > desired.AuthorizedRisk || risk > MaxAllowedRisk)
        {
            LogRefused(logger, key, risk, desired.AuthorizedRisk);
            return Result(
                desired,
                SettingComplianceStatus.Refused,
                setting.Describe(),
                null,
                risk == SettingRisk.Blocked
                    ? $"Blocked: {SettingRiskClassifier.Explain(setting)}"
                    : $"Risk {risk} exceeds the authorised risk {(risk > desired.AuthorizedRisk ? desired.AuthorizedRisk : MaxAllowedRisk)}: {SettingRiskClassifier.Explain(setting)}",
                false);
        }

        if (!_providers.TryGetValue(setting.Kind, out var provider))
        {
            return Result(desired, SettingComplianceStatus.Failed, setting.Describe(), null, $"No provider for {setting.Kind} on this device.", false);
        }

        try
        {
            var observed = provider.Evaluate(setting);
            if (!observed.Applicable)
            {
                return Result(desired, SettingComplianceStatus.NotApplicable, observed.Expected, observed.Actual, "Not present on this device.", HasSnapshot(key));
            }

            if (observed.Compliant)
            {
                return Result(desired, SettingComplianceStatus.Compliant, observed.Expected, observed.Actual, null, HasSnapshot(key));
            }

            if (desired.Mode == EnforcementMode.Monitor)
            {
                return Result(desired, SettingComplianceStatus.Drifted, observed.Expected, observed.Actual, "Monitor mode: not changed.", HasSnapshot(key));
            }

            if (!desired.ApplyAllowed)
            {
                return Result(desired, SettingComplianceStatus.Drifted, observed.Expected, observed.Actual, "Waiting for the maintenance window.", HasSnapshot(key));
            }

            if (provider.SupportsRollback && snapshots.Get(key) is null)
            {
                snapshots.Save(new SettingSnapshot(key, setting.Kind, ContractJson.SerializeSetting(setting), provider.Capture(setting), time.GetUtcNow()));
            }

            provider.Apply(setting);
            var verified = provider.Evaluate(setting);
            if (!verified.Compliant)
            {
                return Result(desired, SettingComplianceStatus.Failed, verified.Expected, verified.Actual, "The change did not take effect (overridden by another mechanism such as Group Policy?).", HasSnapshot(key));
            }

            var remediated = ledger.GetAppliedHash(key) == desired.Hash;
            ledger.SetAppliedHash(key, desired.Hash);
            LogChanged(logger, key, remediated ? "remediated" : "applied", observed.Actual, verified.Actual);
            return Result(
                desired,
                remediated ? SettingComplianceStatus.Remediated : SettingComplianceStatus.Applied,
                verified.Expected,
                verified.Actual,
                remediated ? $"Drift corrected (was {observed.Actual})." : $"Changed from {observed.Actual}.",
                HasSnapshot(key));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, ex, key);
            return Result(desired, SettingComplianceStatus.Failed, setting.Describe(), null, ex.Message, HasSnapshot(key));
        }
    }

    private SettingResult Revert(string key)
    {
        var now = time.GetUtcNow();
        var snapshot = snapshots.Get(key);
        if (snapshot is null)
        {
            ledger.SetAppliedHash(key, null);
            return new SettingResult(key, null, SettingComplianceStatus.RevertUnsupported, null, null, "No snapshot of the original state exists on this device.", false, now);
        }

        if (!_providers.TryGetValue(snapshot.Kind, out var provider) || !provider.SupportsRollback)
        {
            return new SettingResult(key, null, SettingComplianceStatus.RevertUnsupported, null, null, $"Rollback of {snapshot.Kind} settings is not supported.", true, now);
        }

        try
        {
            var setting = ContractJson.DeserializeSetting(snapshot.SettingJson);
            provider.Restore(setting, snapshot.State);
            snapshots.Remove(key);
            ledger.SetAppliedHash(key, null);
            LogReverted(logger, key);
            return new SettingResult(key, null, SettingComplianceStatus.Reverted, null, snapshot.State, $"Restored the state captured {snapshot.CapturedAtUtc:u}.", false, now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, ex, key);
            return new SettingResult(key, null, SettingComplianceStatus.RevertFailed, null, null, ex.Message, true, now);
        }
    }

    private bool HasSnapshot(string key) => snapshots.Get(key) is not null;

    private SettingResult Result(DesiredSetting desired, SettingComplianceStatus status, string? expected, string? actual, string? message, bool rollbackAvailable) =>
        new(desired.Key, desired.PolicyVersionId, status, expected, actual, message, rollbackAvailable, time.GetUtcNow());

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused setting {Key}: risk {Risk} exceeds authorised {Authorized}")]
    private static partial void LogRefused(ILogger logger, string key, SettingRisk risk, SettingRisk authorized);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setting {Key} {Action}: {Before} -> {After}")]
    private static partial void LogChanged(ILogger logger, string key, string action, string before, string after);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setting {Key} reverted to its original state")]
    private static partial void LogReverted(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Error, Message = "Setting {Key} could not be processed")]
    private static partial void LogFailed(ILogger logger, Exception exception, string key);
}
