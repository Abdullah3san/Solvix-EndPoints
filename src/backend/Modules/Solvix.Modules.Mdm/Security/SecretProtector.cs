namespace Solvix.Modules.Mdm.Security;

using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>Encrypts secrets that must be recoverable (they are pushed to devices) but never exposed through the API.</summary>
public interface ISecretProtector
{
    /// <summary>Returns an <c>enc:v1:</c> envelope. Already-protected input is returned unchanged.</summary>
    string Protect(string plaintext);

    /// <summary>Returns the plaintext, or null when <paramref name="protectedValue"/> is null or empty.</summary>
    string? Unprotect(string? protectedValue);

    bool IsProtected(string? value);
}

/// <summary>AES-256-GCM with a random nonce per value; the key comes from <see cref="MdmOptions.SecretEncryptionKey"/>.</summary>
internal sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    // Used only in Development/Testing when no key is configured. Public by design: it protects nothing.
    private static readonly byte[] DevelopmentKey = SHA256.HashData("solvix-mdm-development-only-key"u8);

    private readonly byte[] _key;

    public AesGcmSecretProtector(IOptions<MdmOptions> options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var configured = options.Value.SecretEncryptionKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            {
                _key = DevelopmentKey;
                return;
            }

            throw new InvalidOperationException(
                $"{MdmOptions.SectionName}:{nameof(MdmOptions.SecretEncryptionKey)} must be set (base64, 32 bytes) outside Development.");
        }

        try
        {
            _key = Convert.FromBase64String(configured);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{MdmOptions.SectionName}:{nameof(MdmOptions.SecretEncryptionKey)} is not valid base64.", ex);
        }

        if (_key.Length != 32)
        {
            throw new InvalidOperationException($"{MdmOptions.SectionName}:{nameof(MdmOptions.SecretEncryptionKey)} must decode to exactly 32 bytes.");
        }
    }

    public bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (IsProtected(plaintext))
        {
            return plaintext;
        }

        var data = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        if (!IsProtected(protectedValue))
        {
            throw new CryptographicException("The value is not an encrypted envelope.");
        }

        var raw = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        if (raw.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The encrypted envelope is truncated.");
        }

        var nonce = raw.AsSpan(0, NonceSize);
        var tag = raw.AsSpan(NonceSize, TagSize);
        var cipher = raw.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return System.Text.Encoding.UTF8.GetString(plain);
    }
}
