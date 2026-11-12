namespace Solvix.Agent.Core.Security;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.State;

/// <summary>
/// Device certificate (public, PEM file) + reference to its private key in the
/// <see cref="IDeviceKeyStore"/>. Supplies the certificate for the TLS handshake.
/// </summary>
public sealed class DeviceCredentialStore(IOptions<AgentOptions> options, IDeviceKeyStore keys, IAgentStateStore stateStore) : IDisposable
{
    private readonly Lock _gate = new();
    private X509Certificate2? _current;

    private string Directory => options.Value.ResolveDataDirectory();

    private string CertificatePath => Path.Combine(Directory, "device.crt");

    private string CaPath => Path.Combine(Directory, "ca.crt");

    /// <summary>The active client certificate with its private key, or null when not enrolled.</summary>
    public X509Certificate2? GetClientCertificate()
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                return _current;
            }

            var state = stateStore.Load();
            if (state.KeyName is null || !File.Exists(CertificatePath) || !keys.Exists(state.KeyName))
            {
                return null;
            }

            using var publicOnly = X509Certificate2.CreateFromPem(File.ReadAllText(CertificatePath));
            using var key = keys.OpenKey(state.KeyName);
            _current = publicOnly.CopyWithPrivateKey(key);
            return _current;
        }
    }

    public X509Certificate2? GetCaCertificate() =>
        File.Exists(CaPath) ? X509Certificate2.CreateFromPem(File.ReadAllText(CaPath)) : null;

    /// <summary>Creates a key and a PKCS#10 request for it (the subject is decided by the server).</summary>
    public (string KeyName, string CsrPem) CreateSigningRequest(string hostname)
    {
        using var key = keys.CreateKey(out var keyName);
        var request = new CertificateRequest($"CN={SanitizeCn(hostname)}", key, HashAlgorithmName.SHA256);
        return (keyName, request.CreateSigningRequestPem());
    }

    /// <summary>Stores a newly issued certificate and makes it the active credential.</summary>
    public void Install(string keyName, string certificatePem, IEnumerable<string> caPems)
    {
        ArgumentNullException.ThrowIfNull(caPems);
        using (var check = X509Certificate2.CreateFromPem(certificatePem))
        using (var key = keys.OpenKey(keyName))
        using (var publicKey = check.GetECDsaPublicKey())
        {
            // The certificate must be for the key we generated.
            if (publicKey is null || !publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
            {
                throw new CryptographicException("The issued certificate does not match the device key.");
            }
        }

        lock (_gate)
        {
            AtomicFile.WriteAllText(CertificatePath, certificatePem);
            AtomicFile.WriteAllText(CaPath, string.Join('\n', caPems));
            _current?.Dispose();
            _current = null;
        }
    }

    /// <summary>Forgets the active credential (after revocation) so the agent can re-enroll.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            var state = stateStore.Load();
            if (state.KeyName is not null)
            {
                keys.DeleteKey(state.KeyName);
            }

            if (File.Exists(CertificatePath))
            {
                File.Delete(CertificatePath);
            }

            _current?.Dispose();
            _current = null;
        }
    }

    public void DeleteKey(string keyName) => keys.DeleteKey(keyName);

    public void Dispose()
    {
        _current?.Dispose();
    }

    private static string SanitizeCn(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? "device" : builder.ToString();
    }
}
