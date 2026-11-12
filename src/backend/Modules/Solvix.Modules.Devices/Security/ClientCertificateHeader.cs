namespace Solvix.Modules.Devices.Security;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Converts the client certificate forwarded by a TLS-terminating reverse proxy
/// (base64 DER, or URL-encoded PEM as produced by nginx <c>$ssl_client_escaped_cert</c>).
/// Only used when <c>Solvix:Devices:ForwardedClientCertificateHeader</c> is configured.
/// </summary>
public static class ClientCertificateHeader
{
    public const int MaxLength = 16 * 1024;

    public static X509Certificate2? Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue) || headerValue.Length > MaxLength)
        {
            return null;
        }

        try
        {
            var value = headerValue.Trim();
            if (value.Contains('%', StringComparison.Ordinal))
            {
                value = Uri.UnescapeDataString(value);
            }

            if (value.StartsWith("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                return X509Certificate2.CreateFromPem(value);
            }

            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Encodes a certificate for the header (base64 DER).</summary>
    public static string Encode(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToBase64String(certificate.RawData);
    }
}
