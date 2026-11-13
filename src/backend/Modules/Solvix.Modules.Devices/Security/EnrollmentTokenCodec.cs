namespace Solvix.Modules.Devices.Security;

using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

/// <summary>
/// Opaque enrollment token format: <c>svxe_</c> + base64url(tenantId ‖ tokenId ‖ 256-bit secret).
/// Embedding the tenant id lets the server resolve the tenant scope before the
/// lookup, so the token row is read under Row-Level Security like any other
/// tenant data. A tampered tenant or token id simply finds no matching hash.
/// The prefix makes leaked tokens detectable by secret scanners.
/// </summary>
public static class EnrollmentTokenCodec
{
    public const string Prefix = "svxe_";
    public const int SecretLength = 32;
    private const int PayloadLength = 16 + 16 + SecretLength;

    public static (string Token, byte[] Secret) Create(Guid tenantId, Guid tokenId)
    {
        var secret = RandomNumberGenerator.GetBytes(SecretLength);
        var payload = new byte[PayloadLength];
        tenantId.TryWriteBytes(payload.AsSpan(0, 16));
        tokenId.TryWriteBytes(payload.AsSpan(16, 16));
        secret.CopyTo(payload.AsSpan(32));
        var token = Prefix + Base64Url.EncodeToString(payload);
        CryptographicOperations.ZeroMemory(payload);
        return (token, secret);
    }

    public static bool TryParse(string? token, out Guid tenantId, out Guid tokenId, [NotNullWhen(true)] out byte[]? secret)
    {
        tenantId = Guid.Empty;
        tokenId = Guid.Empty;
        secret = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var value = token.Trim();
        if (!value.StartsWith(Prefix, StringComparison.Ordinal) || value.Length > Prefix.Length + 128)
        {
            return false;
        }

        var payload = new byte[PayloadLength];
        try
        {
            if (Base64Url.DecodeFromChars(value.AsSpan(Prefix.Length), payload) != PayloadLength)
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
        tokenId = new Guid(payload.AsSpan(16, 16));
        secret = payload.AsSpan(32).ToArray();
        CryptographicOperations.ZeroMemory(payload);
        return tenantId != Guid.Empty && tokenId != Guid.Empty;
    }
}
