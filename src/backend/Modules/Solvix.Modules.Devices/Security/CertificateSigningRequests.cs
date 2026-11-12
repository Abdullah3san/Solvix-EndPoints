namespace Solvix.Modules.Devices.Security;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Solvix.SharedKernel.Exceptions;

/// <summary>
/// Validates PKCS#10 requests submitted by agents. The CSR signature proves the
/// agent holds the private key; only the public key is used, the requested
/// subject and extensions are ignored (the server decides the identity).
/// </summary>
public static class CertificateSigningRequests
{
    public const int MaxPemLength = 8 * 1024;

    private const string EcPublicKeyOid = "1.2.840.10045.2.1";
    private const string RsaPublicKeyOid = "1.2.840.113549.1.1.1";
    private const string NistP256Oid = "1.2.840.10045.3.1.7";
    private const string NistP384Oid = "1.3.132.0.34";

    public static PublicKey ParsePublicKey(string? pem, string propertyName = "certificateSigningRequestPem")
    {
        if (string.IsNullOrWhiteSpace(pem) || pem.Length > MaxPemLength)
        {
            throw new ValidationException(propertyName, "A PEM encoded PKCS#10 certificate signing request is required.");
        }

        CertificateRequest request;
        try
        {
            request = CertificateRequest.LoadSigningRequestPem(
                pem,
                HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.Default,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new ValidationException(propertyName, "The certificate signing request is malformed or its signature is invalid.");
        }

        var key = request.PublicKey;
        switch (key.Oid.Value)
        {
            case EcPublicKeyOid:
                using (var ecdsa = key.GetECDsaPublicKey())
                {
                    var curve = ecdsa?.ExportParameters(false).Curve;
                    var oid = curve?.Oid?.Value;
                    if (oid is null && curve?.Oid?.FriendlyName is { } name)
                    {
                        oid = name switch
                        {
                            "nistP256" or "ECDSA_P256" => NistP256Oid,
                            "nistP384" or "ECDSA_P384" => NistP384Oid,
                            _ => null,
                        };
                    }

                    if (oid is not (NistP256Oid or NistP384Oid))
                    {
                        throw new ValidationException(propertyName, "Only ECDSA P-256/P-384 or RSA (2048-4096 bit) keys are accepted.");
                    }
                }

                break;
            case RsaPublicKeyOid:
                using (var rsa = key.GetRSAPublicKey())
                {
                    if (rsa is null || rsa.KeySize is < 2048 or > 4096)
                    {
                        throw new ValidationException(propertyName, "Only ECDSA P-256/P-384 or RSA (2048-4096 bit) keys are accepted.");
                    }
                }

                break;
            default:
                throw new ValidationException(propertyName, "Only ECDSA P-256/P-384 or RSA (2048-4096 bit) keys are accepted.");
        }

        return key;
    }
}
