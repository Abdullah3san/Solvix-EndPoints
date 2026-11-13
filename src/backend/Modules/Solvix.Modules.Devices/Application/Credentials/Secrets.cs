namespace Solvix.Modules.Devices.Application.Credentials;

using System.Security.Cryptography;
using System.Text;
using Solvix.Endpoint.Contracts.Agent;

/// <summary>
/// Bearer secrets for enrolment tokens and device credentials. Each secret
/// carries the id of the row it belongs to (so lookup is an index seek, not a
/// scan) plus 256 bits of randomness. Because the random part is high-entropy,
/// a single SHA-256 is the appropriate storage hash (as for API keys); a slow
/// password hash would only add per-request latency for agents.
/// Format: <c>{prefix}{id:N}_{base64url(32 bytes)}</c>.
/// </summary>
internal static class Secrets
{
    public static string NewEnrollmentToken(Guid tokenId) => Create(AgentAuth.EnrollmentTokenPrefix, tokenId);

    public static string NewDeviceCredential(Guid deviceId) => Create(AgentAuth.DeviceCredentialPrefix, deviceId);

    public static bool TryParse(string? secret, string prefix, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrEmpty(secret) || secret.Length > 200 || !secret.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = secret.AsSpan(prefix.Length);
        var separator = rest.IndexOf('_');
        return separator == 32 && Guid.TryParseExact(rest[..separator], "N", out id) && rest.Length > separator + 1;
    }

    public static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Constant-time comparison of a presented secret with a stored hash.</summary>
    public static bool Verify(string secret, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        var presented = Encoding.ASCII.GetBytes(Hash(secret));
        var stored = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(presented, stored);
    }

    private static string Create(string prefix, Guid id) =>
        $"{prefix}{id:N}_{Base64Url(RandomNumberGenerator.GetBytes(32))}";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
