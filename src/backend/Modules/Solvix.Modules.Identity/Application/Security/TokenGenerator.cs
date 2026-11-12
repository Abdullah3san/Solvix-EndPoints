namespace Solvix.Modules.Identity.Application.Security;

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Opaque bearer secrets (refresh tokens, e-mail links, invitation links, MFA
/// challenges). 256 bits from the system CSPRNG; the database only ever holds
/// the SHA-256 hash, so a leaked table cannot be replayed.
/// </summary>
public static class TokenGenerator
{
    public const int TokenBytes = 32;

    /// <summary>Hex-encoded SHA-256 is always this long.</summary>
    public const int HashLength = 64;

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>Cheap shape check so obviously malformed input never reaches the database.</summary>
    public static bool LooksLikeToken(string? token) =>
        token is { Length: >= 32 and <= 128 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
