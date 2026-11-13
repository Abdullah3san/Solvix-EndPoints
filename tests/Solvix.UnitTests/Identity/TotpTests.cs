namespace Solvix.UnitTests.Identity;

using System.Text;
using Solvix.Modules.Identity.Application.Mfa;

public sealed class TotpTests
{
    // RFC 6238 Appendix B, SHA-1 secret "12345678901234567890".
    private static readonly string RfcSecret = Totp.Base32Encode(Encoding.ASCII.GetBytes("12345678901234567890"));

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Matches_the_rfc_6238_test_vectors(long unixSeconds, string expected)
    {
        // The RFC lists 8-digit values; a 6-digit code is their last six digits.
        var time = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(expected, Totp.Compute(RfcSecret, Totp.StepAt(time)));
    }

    [Fact]
    public void Accepts_the_current_step_and_one_step_of_drift_either_way()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_015);
        var step = Totp.StepAt(now);

        Assert.Equal(step, Totp.Verify(secret, Totp.Compute(secret, step), now, lastUsedStep: null));
        Assert.Equal(step - 1, Totp.Verify(secret, Totp.Compute(secret, step - 1), now, lastUsedStep: null));
        Assert.Equal(step + 1, Totp.Verify(secret, Totp.Compute(secret, step + 1), now, lastUsedStep: null));

        Assert.Null(Totp.Verify(secret, Totp.Compute(secret, step - 2), now, lastUsedStep: null));
        Assert.Null(Totp.Verify(secret, Totp.Compute(secret, step + 2), now, lastUsedStep: null));
    }

    [Fact]
    public void A_code_is_never_accepted_twice_and_older_codes_are_rejected()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_015);
        var step = Totp.StepAt(now);
        var code = Totp.Compute(secret, step);

        Assert.Equal(step, Totp.Verify(secret, code, now, lastUsedStep: step - 1));

        // Same code again (replay) and the previous step's code after a newer one was used.
        Assert.Null(Totp.Verify(secret, code, now, lastUsedStep: step));
        Assert.Null(Totp.Verify(secret, Totp.Compute(secret, step - 1), now, lastUsedStep: step));

        // The next step is still fine.
        Assert.Equal(step + 1, Totp.Verify(secret, Totp.Compute(secret, step + 1), now, lastUsedStep: step));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("١٢٣٤٥٦")]
    public void Rejects_malformed_codes(string? code)
    {
        Assert.Null(Totp.Verify(Totp.GenerateSecret(), code, DateTimeOffset.UtcNow, lastUsedStep: null));
    }

    [Fact]
    public void Tolerates_spaces_and_dashes_as_typed_from_an_authenticator_app()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Compute(secret, Totp.StepAt(now));
        Assert.NotNull(Totp.Verify(secret, $"{code[..3]} {code[3..]}", now, lastUsedStep: null));
        Assert.NotNull(Totp.Verify(secret, $"{code[..3]}-{code[3..]}", now, lastUsedStep: null));
    }

    [Fact]
    public void A_code_for_one_secret_does_not_verify_against_another()
    {
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Compute(Totp.GenerateSecret(), Totp.StepAt(now));
        var other = Totp.GenerateSecret();

        // One in a million would collide by chance; three steps are checked, so retry a few secrets.
        var accepted = Enumerable.Range(0, 5).Count(_ => Totp.Verify(Totp.GenerateSecret(), code, now, null) is not null);
        Assert.True(accepted <= 1, "Codes must be bound to their secret.");
        Assert.Equal(32, other.Length);
    }

    [Fact]
    public void Secrets_are_160_bit_random_and_base32_round_trips()
    {
        var secrets = Enumerable.Range(0, 50).Select(_ => Totp.GenerateSecret()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(50, secrets.Count);
        Assert.All(secrets, s => Assert.Equal(Totp.SecretBytes, Totp.Base32Decode(s).Length));

        var bytes = new byte[] { 0x00, 0xFF, 0x10, 0x20, 0x7F, 0x80, 0x01 };
        Assert.Equal(bytes, Totp.Base32Decode(Totp.Base32Encode(bytes)));
        Assert.Equal("MZXW6YTBOI", Totp.Base32Encode(Encoding.ASCII.GetBytes("foobar")));
        Assert.Throws<FormatException>(() => Totp.Base32Decode("not base32!"));
    }

    [Fact]
    public void Provisioning_uri_escapes_issuer_and_account()
    {
        var uri = Totp.BuildUri("Solvix & Co", "owner+test@acme.io", "ABCDEFGH");
        Assert.StartsWith("otpauth://totp/Solvix%20%26%20Co:owner%2Btest%40acme.io?secret=ABCDEFGH", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Solvix%20%26%20Co", uri, StringComparison.Ordinal);
        Assert.Contains("digits=6", uri, StringComparison.Ordinal);
        Assert.Contains("period=30", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_codes_are_unique_typable_and_normalise()
    {
        var codes = RecoveryCodes.Generate();
        Assert.Equal(RecoveryCodes.Count, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, c =>
        {
            Assert.Matches("^[A-HJKMNP-Z2-9]{4}-[A-HJKMNP-Z2-9]{4}-[A-HJKMNP-Z2-9]{4}$", c);
            Assert.Equal(c.Replace("-", string.Empty, StringComparison.Ordinal), RecoveryCodes.Normalize(c));
            Assert.Equal(RecoveryCodes.Normalize(c), RecoveryCodes.Normalize(" " + c.ToLowerInvariant().Replace('-', ' ') + " "));
        });

        Assert.Null(RecoveryCodes.Normalize("too-short"));
        Assert.Null(RecoveryCodes.Normalize(null));
    }
}
