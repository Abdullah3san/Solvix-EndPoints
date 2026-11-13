namespace Solvix.Agent.Core.Runtime;

using Solvix.Agent.Core.Configuration;
using Solvix.Endpoint.Contracts;

/// <summary>Bound from the <c>Solvix:Agent</c> configuration section.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Solvix:Agent";

    /// <summary>Solvix server base URL, e.g. https://solvix.example.com/.</summary>
    public Uri? ServerUrl { get; set; }

    /// <summary>One-time enrolment token (only needed until the device is enrolled).</summary>
    public string? EnrollmentToken { get; set; }

    /// <summary>Directory for credential, snapshots and journals (Windows: %ProgramData%\Solvix\Agent).</summary>
    public string StateDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Solvix", "Agent");

    public int MinimumPollSeconds { get; set; } = 30;

    /// <summary>Full Windows Update scan interval (also on server request and after restarts).</summary>
    public int ScanIntervalHours { get; set; } = 6;

    /// <summary>Re-evaluation interval for drift detection even when the configuration did not change.</summary>
    public int ConfigurationEvaluationMinutes { get; set; } = 15;

    /// <summary>Local ceiling on configuration risk; "Low" makes the device refuse anything but hardening.</summary>
    public Solvix.Endpoint.Contracts.Configuration.SettingRisk MaxAllowedRisk { get; set; } = Solvix.Endpoint.Contracts.Configuration.SettingRisk.Destructive;
}

/// <summary>Protects the device credential at rest (Windows: DPAPI, machine scope).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedText);
}

/// <summary>For tests and non-Windows development only.</summary>
public sealed class PassthroughSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;

    public string Unprotect(string protectedText) => protectedText;
}

public sealed record PersistedAgentState(
    Guid DeviceId,
    Guid TenantId,
    string ProtectedCredential,
    string? ConfigurationRevision,
    DateTimeOffset? LastScanUtc,
    DateTimeOffset? LastConfigurationEvaluationUtc,
    Dictionary<string, string> AppliedHashes);

/// <summary>Agent state persisted between service restarts, written atomically.</summary>
public sealed class AgentStateStore : IAppliedSettingsLedger
{
    private readonly string? _path;
    private readonly ISecretProtector _protector;
    private PersistedAgentState? _state;

    public AgentStateStore(string? stateDirectory, ISecretProtector protector)
    {
        _protector = protector;
        _path = stateDirectory is null ? null : Path.Combine(stateDirectory, "agent-state.json");
        if (_path is not null && File.Exists(_path))
        {
            _state = ContractJson.Deserialize<PersistedAgentState>(File.ReadAllText(_path));
        }
    }

    public bool IsEnrolled => _state is not null;

    public Guid? DeviceId => _state?.DeviceId;

    public string? ConfigurationRevision => _state?.ConfigurationRevision;

    public DateTimeOffset? LastScanUtc => _state?.LastScanUtc;

    public DateTimeOffset? LastConfigurationEvaluationUtc => _state?.LastConfigurationEvaluationUtc;

    public string? GetCredential() => _state is null ? null : _protector.Unprotect(_state.ProtectedCredential);

    public void SaveEnrollment(Guid deviceId, Guid tenantId, string credential) =>
        Persist(new PersistedAgentState(deviceId, tenantId, _protector.Protect(credential), null, null, null, new Dictionary<string, string>(StringComparer.Ordinal)));

    public void Forget()
    {
        _state = null;
        if (_path is not null && File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    public void RecordScan(DateTimeOffset at) => Update(s => s with { LastScanUtc = at });

    public void RecordConfiguration(string revision, DateTimeOffset at) =>
        Update(s => s with { ConfigurationRevision = revision, LastConfigurationEvaluationUtc = at });

    public string? GetAppliedHash(string key) => _state?.AppliedHashes.GetValueOrDefault(key);

    public void SetAppliedHash(string key, string? hash) => Update(s =>
    {
        var hashes = new Dictionary<string, string>(s.AppliedHashes, StringComparer.Ordinal);
        if (hash is null)
        {
            hashes.Remove(key);
        }
        else
        {
            hashes[key] = hash;
        }

        return s with { AppliedHashes = hashes };
    });

    private void Update(Func<PersistedAgentState, PersistedAgentState> change)
    {
        if (_state is not null)
        {
            Persist(change(_state));
        }
    }

    private void Persist(PersistedAgentState state)
    {
        _state = state;
        if (_path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, ContractJson.Serialize(state));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>Identity and OS facts reported on enrolment and heartbeat.</summary>
public interface IDeviceInfoProvider
{
    string Hostname { get; }

    string? DnsDomain { get; }

    string? HardwareId { get; }

    string OsName { get; }

    string OsVersion { get; }

    string OsBuild { get; }
}
