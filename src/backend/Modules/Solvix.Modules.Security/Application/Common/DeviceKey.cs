namespace Solvix.Modules.Security.Application.Common;

using System.Security.Cryptography;

/// <summary>
/// Credential an agent presents: <c>sxk1.&lt;tenant&gt;.&lt;endpoint&gt;.&lt;secret&gt;</c>. The tenant and endpoint ids are
/// routing hints only; authority comes from the 256-bit random secret, of which only a SHA-256 hash is stored
/// (a fast hash is appropriate because the secret has full entropy and cannot be guessed or dictionary-attacked).
/// </summary>
public static class DeviceKey
{
    public const string Prefix = "sxk1";
    public const string HeaderName = "X-Solvix-Device-Key";
    private const int SecretBytes = 32;
    private const int SecretChars = 43;
    private const int MaxLength = 160;

    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

    public static byte[] Hash(string secret) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));

    public static string Compose(Guid tenantId, Guid endpointId, string secret) =>
        $"{Prefix}.{tenantId:N}.{endpointId:N}.{secret}";

    public static bool TryParse(string? key, out Guid tenantId, out Guid endpointId, out string secret)
    {
        tenantId = Guid.Empty;
        endpointId = Guid.Empty;
        secret = string.Empty;
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength)
        {
            return false;
        }

        var parts = key.Split('.');
        if (parts.Length != 4
            || parts[0] != Prefix
            || !Guid.TryParseExact(parts[1], "N", out tenantId)
            || !Guid.TryParseExact(parts[2], "N", out endpointId)
            || parts[3].Length != SecretChars
            || !parts[3].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }

        secret = parts[3];
        return true;
    }

    /// <summary>Constant-time comparison. Always performs a comparison, even when no stored hash exists.</summary>
    public static bool Verify(byte[]? storedHash, string secret)
    {
        var presented = Hash(secret);
        var expected = storedHash is { Length: 32 } ? storedHash : new byte[32];
        var equal = CryptographicOperations.FixedTimeEquals(presented, expected);
        return equal && storedHash is { Length: 32 };
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
