namespace Solvix.Modules.Identity.Application.Mfa;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Time-based one-time passwords (RFC 6238) as implemented by authenticator
/// apps: HMAC-SHA1, 30 second steps, 6 digits.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;

    /// <summary>Steps accepted either side of "now" to absorb clock drift between phone and server.</summary>
    public const int AllowedDrift = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Compute(string base32Secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
#pragma warning disable CA5350 // RFC 6238 mandates HMAC-SHA1 for interoperability with authenticator apps; HMAC is not affected by SHA-1 collisions.
        HMACSHA1.HashData(Base32Decode(base32Secret), counter, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Checks <paramref name="code"/> against the steps around <paramref name="now"/>.
    /// Steps at or before <paramref name="lastUsedStep"/> are rejected, so a code
    /// that was already accepted (or an older one) can never be replayed.
    /// </summary>
    /// <returns>The matching step, or <c>null</c> when the code is not valid.</returns>
    public static long? Verify(string base32Secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        var candidate = NormalizeCode(code);
        if (candidate is null)
        {
            return null;
        }

        var current = StepAt(now);
        long? matched = null;
        var expected = Encoding.ASCII.GetBytes(candidate);
        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            // Every candidate step is evaluated so the time taken does not depend on which one matched.
            var actual = Encoding.ASCII.GetBytes(Compute(base32Secret, step));
            if (CryptographicOperations.FixedTimeEquals(actual, expected) && (lastUsedStep is null || step > lastUsedStep))
            {
                matched ??= step;
            }
        }

        return matched;
    }

    public static string BuildUri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var digits = new string([.. code.Where(c => !char.IsWhiteSpace(c) && c != '-')]);
        return digits.Length == Digits && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    public static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Base32Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return builder.ToString();
    }

    public static byte[] Base32Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var clean = value.TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var output = new byte[clean.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in clean)
        {
            var digit = Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (digit < 0)
            {
                throw new FormatException("The value is not valid Base32.");
            }

            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                output[index++] = (byte)((buffer >> (bits - 8)) & 0xFF);
                bits -= 8;
            }
        }

        return output;
    }
}

/// <summary>Recovery codes: ten single-use fallbacks for a lost authenticator.</summary>
public static class RecoveryCodes
{
    public const int Count = 10;

    // No 0/O/1/I/L: the codes are typed by hand.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static IReadOnlyList<string> Generate()
    {
        var codes = new string[Count];
        for (var i = 0; i < Count; i++)
        {
            var chars = RandomNumberGenerator.GetItems<char>(Alphabet, 12);
            codes[i] = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
        }

        return codes;
    }

    /// <summary>Canonical form used for hashing: upper-case, separators and spaces removed.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var clean = new string([.. code.Where(char.IsAsciiLetterOrDigit)]).ToUpperInvariant();
        return clean.Length == 12 ? clean : null;
    }
}
