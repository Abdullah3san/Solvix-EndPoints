namespace Solvix.Modules.Identity.Application.Security;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Configuration;

/// <summary>
/// Protects small secrets at rest. TOTP secrets must be recoverable, so they are
/// encrypted (AES-256-GCM); recovery codes only need to be compared, so they
/// are stored as a keyed hash. Both keys are derived from one configured secret
/// with HKDF and distinct labels, so neither can stand in for the other.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);

    /// <summary>Deterministic keyed hash (hex) for values that are looked up, never decrypted.</summary>
    string KeyedHash(string value);
}

internal sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _encryptionKey;
    private readonly byte[] _hashKey;

    public AesGcmSecretProtector(IOptions<IdentitySecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var master = Encoding.UTF8.GetBytes(options.Value.DataProtectionKey);
        if (master.Length < 32)
        {
            throw new InvalidOperationException($"{IdentitySecurityOptions.SectionName}:DataProtectionKey must be at least 32 characters.");
        }

        _encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("solvix:identity:secret-encryption:v1"));
        _hashKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("solvix:identity:keyed-hash:v1"));
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var payload = new byte[NonceSize + data.Length + TagSize];
        var nonce = payload.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Encrypt(nonce, data, payload.AsSpan(NonceSize, data.Length), payload.AsSpan(NonceSize + data.Length, TagSize));
        return $"{Version}.{Convert.ToBase64String(payload)}";
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        var separator = protectedValue.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || protectedValue[..separator] != Version)
        {
            throw new CryptographicException("Unsupported protected value format.");
        }

        var payload = Convert.FromBase64String(protectedValue[(separator + 1)..]);
        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Protected value is truncated.");
        }

        var length = payload.Length - NonceSize - TagSize;
        var plaintext = new byte[length];
        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Decrypt(
            payload.AsSpan(0, NonceSize),
            payload.AsSpan(NonceSize, length),
            payload.AsSpan(NonceSize + length, TagSize),
            plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    public string KeyedHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes(value)));
    }
}
