namespace Solvix.Agent.Core.RemoteManagement;

using System.Text.Json;
using System.Text.Json.Serialization;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.State;
using Microsoft.Extensions.Options;

public sealed record VerificationResult(CommandEnvelope? Envelope, string? RejectionCode, string? Detail)
{
    public bool Accepted => Envelope is not null;

    public static VerificationResult Reject(string code, string detail) => new(null, code, detail);
}

/// <summary>
/// Decides whether a delivered command may run on this device. Nothing about the command is
/// trusted before its signature has been verified against a pinned key; then the envelope must
/// name this tenant and this device, be within its validity window, carry an authorized and
/// (where needed) approved status, name a command this agent implements, and never have been
/// executed before. The transport (mutual TLS) is not relied upon for any of this.
/// </summary>
public sealed class CommandVerifier(
    CommandKeyStore keys,
    CommandReplayStore replays,
    IAgentStateStore state,
    CommandHandlerRegistry handlers,
    IOptions<AgentOptions> options,
    TimeProvider time)
{
    /// <summary>Tolerated clock difference between server and device.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    public VerificationResult Verify(SignedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = keys.Find(command.KeyId);
        if (key is null)
        {
            return VerificationResult.Reject(CommandRejectionCodes.UnknownKey, $"Signing key '{command.KeyId}' is not trusted by this device.");
        }

        var bytes = CommandSignature.Verify(command, key);
        if (bytes is null)
        {
            return VerificationResult.Reject(CommandRejectionCodes.InvalidSignature, "The command signature is invalid.");
        }

        CommandEnvelope? envelope;
        try
        {
            envelope = CommandSignature.Deserialize(bytes);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope is null)
        {
            return VerificationResult.Reject(CommandRejectionCodes.Malformed, "The command could not be read.");
        }

        if (envelope.V != CommandEnvelope.CurrentVersion)
        {
            return VerificationResult.Reject(CommandRejectionCodes.UnsupportedVersion, $"Envelope version {envelope.V} is not supported.");
        }

        if (!string.Equals(envelope.KeyId, command.KeyId, StringComparison.Ordinal))
        {
            return VerificationResult.Reject(CommandRejectionCodes.Malformed, "The envelope names a different key than the signature.");
        }

        var current = state.Load();
        if (current.TenantId is not { } tenantId || envelope.TenantId != tenantId)
        {
            return VerificationResult.Reject(CommandRejectionCodes.WrongTenant, "The command is for another tenant.");
        }

        if (current.DeviceId is not { } deviceId || envelope.DeviceId != deviceId)
        {
            return VerificationResult.Reject(CommandRejectionCodes.WrongDevice, "The command is for another device.");
        }

        var now = time.GetUtcNow();
        if (now > envelope.ExpiresAtUtc + ClockSkew)
        {
            return VerificationResult.Reject(CommandRejectionCodes.Expired, "The command has expired.");
        }

        if (envelope.CreatedAtUtc > now + ClockSkew || envelope.ExpiresAtUtc <= envelope.CreatedAtUtc
            || envelope.ExpiresAtUtc - envelope.CreatedAtUtc > TimeSpan.FromDays(8))
        {
            return VerificationResult.Reject(CommandRejectionCodes.Malformed, "The command validity window is not plausible.");
        }

        if (!string.Equals(envelope.Authorization, CommandAuthorization.Authorized, StringComparison.Ordinal))
        {
            return VerificationResult.Reject(CommandRejectionCodes.NotAuthorized, "The command is not authorized.");
        }

        var approval = envelope.ApprovalStatus;
        if (!(approval == CommandApprovalStatus.NotRequired
              || (approval == CommandApprovalStatus.Approved && envelope.ApprovalId is not null && envelope.ApprovedBy is { } approver && approver != envelope.RequestedBy)))
        {
            return VerificationResult.Reject(CommandRejectionCodes.NotApproved, "The command lacks a valid approval.");
        }

        if (!handlers.CanHandle(envelope.CommandType) || options.Value.RemoteManagement.DisabledCommands.Contains(envelope.CommandType, StringComparer.Ordinal))
        {
            return VerificationResult.Reject(CommandRejectionCodes.NotAllowed, $"'{envelope.CommandType}' is not allowed on this device.");
        }

        if (replays.HasSeen(envelope.ExecutionId, envelope.Nonce))
        {
            return VerificationResult.Reject(CommandRejectionCodes.Replayed, "This command was already received.");
        }

        return new VerificationResult(envelope, null, null);
    }
}

/// <summary>
/// Command signing keys this device trusts (SubjectPublicKeyInfo by key id), pinned on first
/// contact after enrollment over the authenticated channel and kept in the agent's protected
/// data directory. A key that later disappears from the server's set stays trusted only until
/// the agent re-enrolls; a new key is never trusted implicitly once a set is pinned.
/// </summary>
public sealed class CommandKeyStore(IOptions<AgentOptions> options)
{
    private readonly object _gate = new();
    private Dictionary<string, byte[]>? _keys;

    private string FilePath => Path.Combine(options.Value.ResolveDataDirectory(), "command-keys.json");

    public bool IsPinned
    {
        get
        {
            lock (_gate)
            {
                return Load().Count > 0;
            }
        }
    }

    public byte[]? Find(string keyId)
    {
        lock (_gate)
        {
            return Load().TryGetValue(keyId, out var key) ? key : null;
        }
    }

    /// <summary>Pins <paramref name="set"/> if nothing is pinned yet. Returns false if a set was already pinned.</summary>
    public bool PinIfEmpty(CommandKeySet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        lock (_gate)
        {
            if (Load().Count > 0)
            {
                return false;
            }

            var keys = set.Keys
                .Where(k => k.Algorithm == CommandSignature.Algorithm && !string.IsNullOrWhiteSpace(k.KeyId))
                .ToDictionary(k => k.KeyId, k => Convert.FromBase64String(k.PublicKey), StringComparer.Ordinal);
            if (keys.Count == 0)
            {
                return false;
            }

            var json = JsonSerializer.Serialize(
                new PinnedKeys { Keys = keys.ToDictionary(k => k.Key, k => Convert.ToBase64String(k.Value), StringComparer.Ordinal) },
                AgentRemoteJsonContext.Default.PinnedKeys);
            AtomicFile.WriteAllText(FilePath, json);
            _keys = keys;
            return true;
        }
    }

    /// <summary>The pinned keys as a key set: handed to the software executor (which pins them on first use) and used to seed the privileged executor's own copy.</summary>
    public CommandKeySet Export()
    {
        lock (_gate)
        {
            return new CommandKeySet
            {
                Keys = Load().Select(k => new CommandSigningKey { KeyId = k.Key, PublicKey = Convert.ToBase64String(k.Value) }).ToList(),
            };
        }
    }

    /// <summary>Forgets pinned keys (re-enrollment into a new identity).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }

            _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }
    }

    private Dictionary<string, byte[]> Load()
    {
        if (_keys is not null)
        {
            return _keys;
        }

        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (File.Exists(FilePath))
        {
            var pinned = JsonSerializer.Deserialize(File.ReadAllText(FilePath), AgentRemoteJsonContext.Default.PinnedKeys);
            foreach (var (id, key) in pinned?.Keys ?? [])
            {
                _keys[id] = Convert.FromBase64String(key);
            }
        }

        return _keys;
    }
}

/// <summary>
/// Executions this device has accepted, kept until their expiry has passed (after which the
/// expiry check alone rejects a replay). Persisted, so a restart does not reopen the window.
/// </summary>
public sealed class CommandReplayStore(IOptions<AgentOptions> options, TimeProvider time)
{
    public const int MaxEntries = 10_000;

    private readonly object _gate = new();
    private Dictionary<Guid, SeenCommand>? _seen;

    private string FilePath => Path.Combine(options.Value.ResolveDataDirectory(), "executed-commands.json");

    public bool HasSeen(Guid executionId, string nonce)
    {
        lock (_gate)
        {
            var seen = Load();
            return seen.ContainsKey(executionId) || seen.Values.Any(s => string.Equals(s.Nonce, nonce, StringComparison.Ordinal));
        }
    }

    /// <summary>Records an accepted command before it runs. Returns false if it was already recorded (concurrent duplicate).</summary>
    public bool TryRecord(CommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            var seen = Load();
            if (seen.ContainsKey(envelope.ExecutionId))
            {
                return false;
            }

            var now = time.GetUtcNow();
            foreach (var expired in seen.Where(s => s.Value.ExpiresAtUtc + CommandVerifier.ClockSkew < now).Select(s => s.Key).ToList())
            {
                seen.Remove(expired);
            }

            if (seen.Count >= MaxEntries)
            {
                // Bounded: drop the soonest-expiring entries; the expiry check still covers them.
                foreach (var oldest in seen.OrderBy(s => s.Value.ExpiresAtUtc).Take(seen.Count - MaxEntries + 1).Select(s => s.Key).ToList())
                {
                    seen.Remove(oldest);
                }
            }

            seen[envelope.ExecutionId] = new SeenCommand { Nonce = envelope.Nonce, ExpiresAtUtc = envelope.ExpiresAtUtc, Sequence = envelope.Sequence };
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(new SeenCommands { Items = seen }, AgentRemoteJsonContext.Default.SeenCommands));
            return true;
        }
    }

    private Dictionary<Guid, SeenCommand> Load()
    {
        if (_seen is not null)
        {
            return _seen;
        }

        _seen = File.Exists(FilePath)
            ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), AgentRemoteJsonContext.Default.SeenCommands)?.Items ?? []
            : [];
        return _seen;
    }
}

public sealed record PinnedKeys
{
    public Dictionary<string, string> Keys { get; init; } = [];
}

public sealed record SeenCommands
{
    public Dictionary<Guid, SeenCommand> Items { get; init; } = [];
}

public sealed record SeenCommand
{
    public required string Nonce { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public long Sequence { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PinnedKeys))]
[JsonSerializable(typeof(SeenCommands))]
internal sealed partial class AgentRemoteJsonContext : JsonSerializerContext
{
}
