namespace Solvix.Agent.Core.Communication;

using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Security;

/// <summary>Creates the HTTP handler used to talk to the server.</summary>
public interface IAgentTransport
{
    HttpMessageHandler CreateHandler();

    /// <summary>
    /// Identifies the client credential handlers are created with (e.g. certificate
    /// thumbprint). When it changes, pooled TLS connections must be discarded:
    /// a connection opened before enrollment or renewal carries the old (or no)
    /// client certificate for its whole lifetime.
    /// </summary>
    string? CredentialVersion => null;
}

/// <summary>
/// HTTPS with TLS 1.2/1.3 only. The device certificate is offered in the TLS
/// handshake (mutual TLS) and re-read on every new connection, so a renewed
/// certificate is picked up without restarting. The server certificate must be
/// trusted by the OS or match a locally configured SHA-256 pin.
/// </summary>
public sealed class MutualTlsTransport(IOptions<AgentOptions> options, DeviceCredentialStore credentials) : IAgentTransport
{
    public string? CredentialVersion => credentials.GetClientCertificate()?.Thumbprint;

    public HttpMessageHandler CreateHandler()
    {
        var pins = options.Value.TrustedServerCertificateSha256
            .Select(p => p.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant())
            .Where(p => p.Length == 64)
            .ToHashSet(StringComparer.Ordinal);

        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = false,
            UseProxy = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.Offline, // cached CRLs only: a CRL outage must not cut off the fleet
                LocalCertificateSelectionCallback = (_, _, _, _, _) => credentials.GetClientCertificate()!,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (errors == SslPolicyErrors.None)
                    {
                        return true;
                    }

                    // Pinning accepts a private certificate, but never a name mismatch.
                    return certificate is not null
                        && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
                        && pins.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256));
                },
            },
        };
    }
}
