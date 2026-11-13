namespace Solvix.Agent.Contracts.RemoteManagement;

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// The closed set of command types. The server can only create these, and the agent only
/// executes types it has a local handler for: there is no generic "run this" command.
/// </summary>
public static class CommandTypes
{
    public const string SystemInfo = "system.info";
    public const string Processes = "system.processes";
    public const string Services = "system.services";
    public const string Uptime = "system.uptime";
    public const string LoggedInUsers = "system.logged_in_users";
    public const string Restart = "system.restart";
    public const string Shutdown = "system.shutdown";
    public const string Lock = "system.lock";
    public const string Logoff = "system.logoff";

    /// <summary>Asks the agent to cancel a running execution (parameter <c>executionId</c>).</summary>
    public const string Cancel = "command.cancel";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SystemInfo, Processes, Services, Uptime, LoggedInUsers, Restart, Shutdown, Lock, Logoff, Cancel,
    };
}

public static class CommandAuthorization
{
    public const string Authorized = "Authorized";
}

public static class CommandApprovalStatus
{
    public const string NotRequired = "NotRequired";
    public const string Approved = "Approved";
}

/// <summary>
/// What the server asks a device to do. It is serialized once by the server
/// (<see cref="AgentJsonContext"/>, fixed property order), signed over exactly
/// those bytes, and the agent verifies the bytes before deserializing them.
/// </summary>
public sealed record CommandEnvelope
{
    public const int CurrentVersion = 1;

    public int V { get; init; } = CurrentVersion;

    public required Guid ExecutionId { get; init; }

    public required Guid TenantId { get; init; }

    public required Guid DeviceId { get; init; }

    public required Guid RequestedBy { get; init; }

    public required string CommandType { get; init; }

    public int CommandVersion { get; init; } = 1;

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>Per-device, strictly increasing.</summary>
    public required long Sequence { get; init; }

    /// <summary>16 random bytes, base64url.</summary>
    public required string Nonce { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public required string Authorization { get; init; }

    public required string ApprovalStatus { get; init; }

    public Guid? ApprovalId { get; init; }

    public Guid? ApprovedBy { get; init; }

    public required Guid AuditId { get; init; }

    public required string KeyId { get; init; }
}

/// <summary>A signed envelope on the wire.</summary>
public sealed record SignedCommand
{
    /// <summary>Base64url of the canonical envelope bytes.</summary>
    public required string Envelope { get; init; }

    /// <summary>Base64url ECDSA P-256 / SHA-256 signature (IEEE P1363) over the envelope bytes.</summary>
    public required string Signature { get; init; }

    public required string KeyId { get; init; }
}

public sealed record AgentCommandBatch
{
    public IReadOnlyList<SignedCommand> Commands { get; init; } = [];
}

/// <summary>Sent as soon as the agent has verified (or refused) a command, before executing it.</summary>
public sealed record CommandAcknowledgement
{
    public bool Accepted { get; init; }

    /// <summary>Why the agent refused the command (see <see cref="CommandRejectionCodes"/>).</summary>
    public string? RejectionCode { get; init; }

    public string? Detail { get; init; }
}

public static class CommandRejectionCodes
{
    public const string InvalidSignature = "invalid_signature";
    public const string UnknownKey = "unknown_key";
    public const string Malformed = "malformed";
    public const string WrongTenant = "wrong_tenant";
    public const string WrongDevice = "wrong_device";
    public const string Expired = "expired";
    public const string Replayed = "replayed";
    public const string NotAuthorized = "not_authorized";
    public const string NotApproved = "not_approved";
    public const string NotAllowed = "not_allowed";
    public const string UnsupportedVersion = "unsupported_version";
}

public enum CommandOutcome
{
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
    TimedOut = 4,
}

public sealed record CommandResultReport
{
    public const int MaxOutputLength = 256 * 1024;

    public required CommandOutcome Outcome { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>Handler output (JSON for structured results), at most <see cref="MaxOutputLength"/> characters.</summary>
    public string? Output { get; init; }

    public bool OutputTruncated { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; }
}

/// <summary>Public keys the agent accepts command signatures from.</summary>
public sealed record CommandKeySet
{
    public IReadOnlyList<CommandSigningKey> Keys { get; init; } = [];
}

public sealed record CommandSigningKey
{
    public required string KeyId { get; init; }

    public string Algorithm { get; init; } = CommandSignature.Algorithm;

    /// <summary>SubjectPublicKeyInfo (DER), base64.</summary>
    public required string PublicKey { get; init; }
}

/// <summary>Canonical serialization and ECDSA P-256 signatures, shared by the signer and the verifiers.</summary>
public static class CommandSignature
{
    public const string Algorithm = "ES256";

    public static byte[] Serialize(CommandEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, AgentJsonContext.Default.CommandEnvelope);

    public static CommandEnvelope? Deserialize(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize(bytes, AgentJsonContext.Default.CommandEnvelope);

    public static SignedCommand Sign(CommandEnvelope envelope, ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(key);
        var bytes = Serialize(envelope);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignedCommand
        {
            Envelope = Base64Url.EncodeToString(bytes),
            Signature = Base64Url.EncodeToString(signature),
            KeyId = envelope.KeyId,
        };
    }

    /// <summary>Verifies <paramref name="command"/> against a SubjectPublicKeyInfo and returns the envelope bytes, or <c>null</c>.</summary>
    public static byte[]? Verify(SignedCommand command, ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        ArgumentNullException.ThrowIfNull(command);
        byte[] bytes;
        byte[] signature;
        try
        {
            bytes = Base64Url.DecodeFromChars(command.Envelope);
            signature = Base64Url.DecodeFromChars(command.Signature);
        }
        catch (FormatException)
        {
            return null;
        }

        if (signature.Length != 64 || bytes.Length == 0 || bytes.Length > 64 * 1024)
        {
            return null;
        }

        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (key.KeySize != 256)
        {
            return null;
        }

        return key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            ? bytes
            : null;
    }

    public static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
}
