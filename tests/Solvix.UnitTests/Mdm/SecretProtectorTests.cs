namespace Solvix.UnitTests.Mdm;

using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Solvix.Modules.Mdm;
using Solvix.Modules.Mdm.Security;

public sealed class SecretProtectorTests
{
    private static AesGcmSecretProtector Create(string? key, string environment = "Production")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        return new AesGcmSecretProtector(Options.Create(new MdmOptions { SecretEncryptionKey = key }), env);
    }

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Round_trips_and_never_reuses_a_nonce()
    {
        var protector = Create(NewKey());
        var a = protector.Protect("correct horse battery staple");
        var b = protector.Protect("correct horse battery staple");
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("correct", a, StringComparison.Ordinal);
        Assert.StartsWith("enc:v1:", a, StringComparison.Ordinal);
        Assert.Equal("correct horse battery staple", protector.Unprotect(a));
        Assert.Equal(a, protector.Protect(a)); // already protected input is not double-encrypted
        Assert.Null(protector.Unprotect(null));
        Assert.Null(protector.Unprotect(string.Empty));
    }

    [Fact]
    public void Tampering_and_wrong_keys_are_detected()
    {
        var key = NewKey();
        var protector = Create(key);
        var value = protector.Protect("secret");
        var raw = Convert.FromBase64String(value["enc:v1:".Length..]);
        raw[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("enc:v1:" + Convert.ToBase64String(raw)));
        Assert.ThrowsAny<CryptographicException>(() => Create(NewKey()).Unprotect(value));
        Assert.Throws<CryptographicException>(() => protector.Unprotect("plaintext"));
    }

    [Fact]
    public void A_key_is_mandatory_outside_development_and_must_be_32_bytes()
    {
        Assert.Throws<InvalidOperationException>(() => Create(null));
        Assert.Throws<InvalidOperationException>(() => Create(Convert.ToBase64String(new byte[16])));
        Assert.Throws<InvalidOperationException>(() => Create("not base64!"));
        Assert.Equal("x", Create(null, "Development").Unprotect(Create(null, "Development").Protect("x")));
        Assert.Equal("x", Create(null, "Testing").Unprotect(Create(null, "Testing").Protect("x")));
    }
}
