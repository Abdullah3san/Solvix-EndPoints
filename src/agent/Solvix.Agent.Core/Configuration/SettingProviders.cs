namespace Solvix.Agent.Core.Configuration;

using Solvix.Endpoint.Contracts.Configuration;

/// <summary>Result of comparing one desired setting with the device.</summary>
/// <param name="Applicable">False when the target does not exist and cannot be created (e.g. an unknown service).</param>
/// <param name="Compliant">True when the device already matches the desired state.</param>
/// <param name="Expected">Human-readable desired value.</param>
/// <param name="Actual">Human-readable current value.</param>
public sealed record Observation(bool Applicable, bool Compliant, string Expected, string Actual);

/// <summary>
/// Reads, applies, snapshots and restores one kind of setting through a
/// Windows-supported management interface (SCM, registry, firewall COM API,
/// power management API). Implementations must be idempotent: applying an
/// already compliant setting is a no-op.
/// </summary>
public interface ISettingProvider
{
    SettingKind Kind { get; }

    /// <summary>Whether the previous state can be captured and restored.</summary>
    bool SupportsRollback { get; }

    Observation Evaluate(ConfigurationSetting desired);

    void Apply(ConfigurationSetting desired);

    /// <summary>Captures the current state of exactly what <paramref name="desired"/> would change, as JSON.</summary>
    string Capture(ConfigurationSetting desired);

    /// <summary>Restores a state produced by <see cref="Capture"/>.</summary>
    void Restore(ConfigurationSetting desired, string capturedState);
}

/// <summary>The device state captured before Solvix first changed a setting key.</summary>
public sealed record SettingSnapshot(string Key, SettingKind Kind, string SettingJson, string State, DateTimeOffset CapturedAtUtc);

/// <summary>Persistent snapshot storage (one snapshot per key: the original, pre-Solvix state).</summary>
public interface ISnapshotStore
{
    SettingSnapshot? Get(string key);

    void Save(SettingSnapshot snapshot);

    void Remove(string key);
}

public sealed class InMemorySnapshotStore : ISnapshotStore
{
    private readonly Dictionary<string, SettingSnapshot> _snapshots = new(StringComparer.Ordinal);

    public SettingSnapshot? Get(string key) => _snapshots.GetValueOrDefault(key);

    public void Save(SettingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshots[snapshot.Key] = snapshot;
    }

    public void Remove(string key) => _snapshots.Remove(key);
}

/// <summary>
/// Stores snapshots as JSON files (one per key) in the agent's state directory,
/// which the Windows host restricts to SYSTEM and Administrators.
/// </summary>
public sealed class FileSnapshotStore(string directory) : ISnapshotStore
{
    public SettingSnapshot? Get(string key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? Solvix.Endpoint.Contracts.ContractJson.Deserialize<SettingSnapshot>(File.ReadAllText(path)) : null;
    }

    public void Save(SettingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Directory.CreateDirectory(directory);
        var path = PathFor(snapshot.Key);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Solvix.Endpoint.Contracts.ContractJson.Serialize(snapshot));
        File.Move(temp, path, overwrite: true);
    }

    public void Remove(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(string key) =>
        Path.Combine(directory, Solvix.Endpoint.Contracts.ContractJson.Sha256(key)[..40] + ".json");
}
