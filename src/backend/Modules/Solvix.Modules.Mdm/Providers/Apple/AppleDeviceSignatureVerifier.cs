namespace Solvix.Modules.Mdm.Providers.Apple;

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

/// <summary>
/// Verifies the <c>Mdm-Signature</c> header: a detached CMS (PKCS#7) signature of the request body made with
/// the device's identity certificate (issued over SCEP during enrollment). The first verified message binds the
/// certificate to the device record; later messages must be signed by the same certificate.
/// </summary>
public interface IAppleDeviceSignatureVerifier
{
    /// <summary>Returns the SHA-256 thumbprint of the signer, or throws <see cref="AppleSignatureException"/>.</summary>
    string Verify(ReadOnlySpan<byte> body, string? mdmSignatureHeader);
}

internal sealed class AppleDeviceSignatureVerifier(IOptions<MdmOptions> options) : IAppleDeviceSignatureVerifier
{
    public string Verify(ReadOnlySpan<byte> body, string? mdmSignatureHeader)
    {
        if (string.IsNullOrWhiteSpace(mdmSignatureHeader))
        {
            throw new AppleSignatureException("The Mdm-Signature header is missing.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(mdmSignatureHeader.Trim());
        }
        catch (FormatException ex)
        {
            throw new AppleSignatureException("The Mdm-Signature header is not valid base64.", ex);
        }

        try
        {
            var cms = new SignedCms(new ContentInfo(body.ToArray()), detached: true);
            cms.Decode(signature);
            if (cms.SignerInfos.Count != 1)
            {
                throw new AppleSignatureException("Exactly one signer is required.");
            }

            // Cryptographic validity of the signature; trust in the certificate is decided below.
            cms.CheckSignature(verifySignatureOnly: true);
            var certificate = cms.SignerInfos[0].Certificate
                ?? throw new AppleSignatureException("The signature does not carry the signer certificate.");

            EnsureTrusted(certificate, cms.Certificates);
            return Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
        }
        catch (CryptographicException ex)
        {
            throw new AppleSignatureException("The device signature is invalid.", ex);
        }
    }

    private void EnsureTrusted(X509Certificate2 certificate, X509Certificate2Collection extra)
    {
        var apple = options.Value.Apple;
        if (string.IsNullOrWhiteSpace(apple.IdentityCaCertificatePem))
        {
            if (apple.AllowUntrustedDeviceIdentity)
            {
                return;
            }

            throw new AppleSignatureException("No identity CA is configured; devices cannot be authenticated.");
        }

        using var ca = X509Certificate2.CreateFromPem(apple.IdentityCaCertificatePem);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        if (!chain.Build(certificate))
        {
            throw new AppleSignatureException("The device certificate does not chain to the configured identity CA.");
        }
    }
}

public sealed class AppleSignatureException : Exception
{
    public AppleSignatureException(string message)
        : base(message)
    {
    }

    public AppleSignatureException()
    {
    }

    public AppleSignatureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
