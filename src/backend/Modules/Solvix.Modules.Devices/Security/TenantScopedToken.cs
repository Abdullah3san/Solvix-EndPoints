namespace Solvix.Modules.Devices.Security;

using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

/// <summary>
/// Opaque, prefixed token <c>{prefix}base64url(tenantId ‖ id ‖ 256-bit secret)</c>. The tenant id lets the server
/// bind an anonymous request to its tenant before reading the row (so the lookup runs under Row-Level
/// Security); the secret is compared in constant time against a stored value. Same layout as the one-time
/// enrollment token; used for deployment profile codes and enrollment poll tokens.
/// </summary>
public static class TenantScopedToken
{
    public const int SecretLength = 32;
    private const int PayloadLength = 16 + 16 + SecretLength;

    public static (string Token, byte[] Secret) Create(string prefix, Guid tenantId, Guid id) =>
        Create(prefix, tenantId, id, RandomNumberGenerator.GetBytes(SecretLength));

    public static (string Token, byte[] Secret) Create(string prefix, Guid tenantId, Guid id, byte[] secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != SecretLength)
        {
            throw new ArgumentException("Invalid secret length.", nameof(secret));
        }

        var payload = new byte[PayloadLength];
        tenantId.TryWriteBytes(payload.AsSpan(0, 16));
        id.TryWriteBytes(payload.AsSpan(16, 16));
        secret.CopyTo(payload.AsSpan(32));
        var token = prefix + Base64Url.EncodeToString(payload);
        CryptographicOperations.ZeroMemory(payload);
        return (token, secret);
    }

    public static bool TryParse(string prefix, string? token, out Guid tenantId, out Guid id, [NotNullWhen(true)] out byte[]? secret)
    {
        tenantId = Guid.Empty;
        id = Guid.Empty;
        secret = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var value = token.Trim();
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length > prefix.Length + 128)
        {
            return false;
        }

        var payload = new byte[PayloadLength];
        try
        {
            if (Base64Url.DecodeFromChars(value.AsSpan(prefix.Length), payload) != PayloadLength)
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        tenantId = new Guid(payload.AsSpan(0, 16));
        id = new Guid(payload.AsSpan(16, 16));
        secret = payload.AsSpan(32).ToArray();
        CryptographicOperations.ZeroMemory(payload);
        return tenantId != Guid.Empty && id != Guid.Empty;
    }
}

/// <summary>Token prefixes of the approval-based enrollment.</summary>
public static class DeploymentTokens
{
    /// <summary>Deployment profile code embedded in a generic installer. Not a secret credential: requests are held for approval.</summary>
    public const string ProfilePrefix = "svxp_";

    /// <summary>One-time poll token of a pending enrollment.</summary>
    public const string PollPrefix = "svxw_";

    public static bool IsProfileCode(string? token) => token is not null && token.TrimStart().StartsWith(ProfilePrefix, StringComparison.Ordinal);
}
