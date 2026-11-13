namespace Solvix.UnitTests.Identity;

using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;

public sealed class SecurityPrimitivesTests
{
    private static AesGcmSecretProtector Protector(string key = "unit-test-data-protection-key-0123456789") =>
        new(Options.Create(new IdentitySecurityOptions { DataProtectionKey = key }));

    [Fact]
    public void Secrets_round_trip_and_are_not_stored_in_the_clear()
    {
        var protector = Protector();
        var cipher = protector.Protect("JBSWY3DPEHPK3PXP");

        Assert.StartsWith("v1.", cipher, StringComparison.Ordinal);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", cipher, StringComparison.Ordinal);
        Assert.Equal("JBSWY3DPEHPK3PXP", protector.Unprotect(cipher));

        // A fresh nonce every time: the same secret never produces the same ciphertext.
        Assert.NotEqual(cipher, protector.Protect("JBSWY3DPEHPK3PXP"));
    }

    [Fact]
    public void Tampered_or_foreign_ciphertext_is_rejected()
    {
        var protector = Protector();
        var cipher = protector.Protect("secret-value");
        var bytes = Convert.FromBase64String(cipher[3..]);
        bytes[^1] ^= 0x01;
        var tampered = "v1." + Convert.ToBase64String(bytes);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(tampered));
        Assert.ThrowsAny<CryptographicException>(() => Protector("a-completely-different-key-0123456789abcdef").Unprotect(cipher));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("v9." + cipher[3..]));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("v1.AAAA"));
    }

    [Fact]
    public void Keyed_hash_is_stable_per_key_and_differs_between_keys()
    {
        var a = Protector();
        var b = Protector("a-completely-different-key-0123456789abcdef");
        Assert.Equal(a.KeyedHash("ABCD2345EFGH"), a.KeyedHash("ABCD2345EFGH"));
        Assert.NotEqual(a.KeyedHash("ABCD2345EFGH"), a.KeyedHash("ABCD2345EFGJ"));
        Assert.NotEqual(a.KeyedHash("ABCD2345EFGH"), b.KeyedHash("ABCD2345EFGH"));
        Assert.Equal(64, a.KeyedHash("x").Length);
    }

    [Fact]
    public void Short_keys_are_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Protector("too-short"));
    }

    [Fact]
    public void Tokens_are_unpredictable_and_only_their_hash_is_comparable()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => TokenGenerator.NewToken()).ToList();
        Assert.Equal(200, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, t =>
        {
            Assert.Equal(43, t.Length); // 32 bytes, base64url, no padding
            Assert.True(TokenGenerator.LooksLikeToken(t));
            Assert.Equal(TokenGenerator.HashLength, TokenGenerator.Hash(t).Length);
            Assert.NotEqual(t, TokenGenerator.Hash(t));
        });

        Assert.Equal(TokenGenerator.Hash(tokens[0]), TokenGenerator.Hash(tokens[0]));
        Assert.NotEqual(TokenGenerator.Hash(tokens[0]), TokenGenerator.Hash(tokens[1]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("contains spaces contains spaces contains spaces")]
    [InlineData("'; DROP TABLE users; -- padding padding padding")]
    public void Malformed_tokens_are_recognised_before_any_lookup(string? token)
    {
        Assert.False(TokenGenerator.LooksLikeToken(token));
    }

    [Fact]
    public void Passwords_are_hashed_with_salted_pbkdf2_at_the_configured_work_factor()
    {
        var options = new IdentitySecurityOptions();
        Assert.True(options.Password.HashIterations >= 210_000);

        var hasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { IterationCount = options.Password.HashIterations }));
        var user = User.Create("owner@acme.io", "Owner", "pending", DateTimeOffset.UtcNow);
        var first = hasher.HashPassword(user, "Passw0rd!Secure");
        var second = hasher.HashPassword(user, "Passw0rd!Secure");

        Assert.DoesNotContain("Passw0rd", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second); // per-password random salt
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user, first, "Passw0rd!Secure"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, first, "passw0rd!secure"));

        // A hash made with a weaker work factor still verifies but is flagged for upgrade.
        var weak = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { IterationCount = 10_000 })).HashPassword(user, "Passw0rd!Secure");
        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, hasher.VerifyHashedPassword(user, weak, "Passw0rd!Secure"));
    }

    [Fact]
    public void Lockout_duration_doubles_with_each_consecutive_lockout_up_to_the_cap()
    {
        var lockout = new Solvix.Modules.Identity.Configuration.LockoutOptions { DurationMinutes = 15, MaxDurationMinutes = 120 };
        Assert.Equal(TimeSpan.FromMinutes(15), lockout.DurationFor(0));
        Assert.Equal(TimeSpan.FromMinutes(30), lockout.DurationFor(1));
        Assert.Equal(TimeSpan.FromMinutes(60), lockout.DurationFor(2));
        Assert.Equal(TimeSpan.FromMinutes(120), lockout.DurationFor(3));
        Assert.Equal(TimeSpan.FromMinutes(120), lockout.DurationFor(50));
    }
}
