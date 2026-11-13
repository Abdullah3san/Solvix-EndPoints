namespace Solvix.UnitTests.Identity;

using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Configuration;

public sealed class PasswordPolicyTests
{
    private static readonly PasswordRules Baseline = PasswordRules.From(new IdentitySecurityOptions().Baseline());

    [Fact]
    public void Baseline_accepts_a_reasonable_password()
    {
        Assert.Empty(PasswordPolicy.Validate("Passw0rd!Secure", Baseline, "owner@acme.io", "Alex Owner"));
        Assert.Equal(10, Baseline.MinLength);
    }

    [Theory]
    [InlineData("short1a", "at least 10")]
    [InlineData("NOLOWERCASE123", "lower-case")]
    [InlineData("nodigitshereatall", "digit")]
    public void Baseline_rejects_passwords_that_miss_a_rule(string password, string expectedFragment)
    {
        var errors = PasswordPolicy.Validate(password, Baseline);
        Assert.Contains(errors, e => e.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_configured_requirement_is_enforced()
    {
        var strict = new PasswordRules(16, RequireUppercase: true, RequireLowercase: true, RequireDigit: true, RequireSymbol: true, 0, 0);
        Assert.Empty(PasswordPolicy.Validate("Correct-Horse-7-Battery", strict));

        Assert.Contains(PasswordPolicy.Validate("correct-horse-7-battery", strict), e => e.Contains("upper-case", StringComparison.Ordinal));
        Assert.Contains(PasswordPolicy.Validate("CorrectHorse7Battery", strict), e => e.Contains("symbol", StringComparison.Ordinal));
        Assert.Contains(PasswordPolicy.Validate("Correct-Horse-Battery", strict), e => e.Contains("digit", StringComparison.Ordinal));
        Assert.Contains(PasswordPolicy.Validate("Short-1a", strict), e => e.Contains("at least 16", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("password123")]
    [InlineData("PASSWORD123")]
    [InlineData("qwertyuiop1")]
    [InlineData("Welcome12345")]
    public void Rejects_common_passwords_regardless_of_case(string password)
    {
        var relaxed = new PasswordRules(8, false, false, false, false, 0, 0);
        Assert.Contains(PasswordPolicy.Validate(password, relaxed), e => e.Contains("too common", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_passwords_built_from_the_users_own_name_or_address()
    {
        Assert.Contains(PasswordPolicy.Validate("jordan.lee-2026x", Baseline, "jordan.lee@acme.io", null), e => e.Contains("name or e-mail", StringComparison.Ordinal));
        Assert.Contains(PasswordPolicy.Validate("xx-JordanLee-99", Baseline, null, "Jordan Lee"), e => e.Contains("name or e-mail", StringComparison.Ordinal));

        // Fragments shorter than four characters are ignored, or "Al" would block half the dictionary.
        Assert.Empty(PasswordPolicy.Validate("signal-tower-42", Baseline, "al@acme.io", "Al"));
    }

    [Fact]
    public void Rejects_empty_and_overlong_passwords()
    {
        Assert.NotEmpty(PasswordPolicy.Validate(null, Baseline));
        Assert.NotEmpty(PasswordPolicy.Validate(string.Empty, Baseline));
        Assert.Contains(
            PasswordPolicy.Validate("a1" + new string('x', PasswordRules.MaxLength), Baseline),
            e => e.Contains("at most", StringComparison.Ordinal));
    }

    [Fact]
    public void Strictest_takes_the_harder_value_of_every_rule()
    {
        var a = new PasswordRules(10, RequireUppercase: false, RequireLowercase: true, RequireDigit: true, RequireSymbol: false, HistoryCount: 5, MaxAgeDays: 0);
        var b = new PasswordRules(16, RequireUppercase: true, RequireLowercase: false, RequireDigit: false, RequireSymbol: true, HistoryCount: 2, MaxAgeDays: 90);
        var c = new PasswordRules(12, false, false, false, false, HistoryCount: 12, MaxAgeDays: 30);

        var merged = a.Strictest(b).Strictest(c);
        Assert.Equal(new PasswordRules(16, true, true, true, true, 12, 30), merged);

        // Order does not matter, and "never expires" (0) loses to any real limit.
        Assert.Equal(merged, c.Strictest(a).Strictest(b));
        Assert.Equal(0, a.Strictest(a).MaxAgeDays);
    }
}
