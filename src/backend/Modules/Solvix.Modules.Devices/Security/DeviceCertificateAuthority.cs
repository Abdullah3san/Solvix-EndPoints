namespace Solvix.Modules.Devices.Security;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Configuration;

public interface IDeviceCertificateAuthority
{
    /// <summary>The CA certificate (public part) agents and the TLS layer trust.</summary>
    X509Certificate2 Certificate { get; }

    string CertificatePem { get; }

    /// <summary>Issues a client-authentication certificate binding <paramref name="publicKey"/> to a device.</summary>
    IssuedDeviceCertificate Issue(PublicKey publicKey, Guid tenantId, Guid deviceId, DateTimeOffset notBefore, DateTimeOffset notAfter);
}

public sealed record IssuedDeviceCertificate(X509Certificate2 Certificate, string Pem, string Thumbprint, string SerialNumber, DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc);

/// <summary>
/// Platform device CA (ECDSA). The private key is loaded once into memory from
/// a PKCS#12 bundle; it is never logged or returned by any API.
/// </summary>
internal sealed partial class DeviceCertificateAuthority : IDeviceCertificateAuthority, IDisposable
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    private readonly X509Certificate2 _ca;
    private readonly ECDsa _key;

    public DeviceCertificateAuthority(IOptions<DevicesOptions> options, ILogger<DeviceCertificateAuthority> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var ca = options.Value.CertificateAuthority;
        _ca = Load(ca, logger);
        _key = _ca.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("The device CA bundle must contain an ECDSA private key.");
        CertificatePem = _ca.ExportCertificatePem();
        LogLoaded(logger, _ca.Subject, _ca.GetCertHashString(HashAlgorithmName.SHA256), _ca.NotAfter);
    }

    public X509Certificate2 Certificate => _ca;

    public string CertificatePem { get; }

    public IssuedDeviceCertificate Issue(PublicKey publicKey, Guid tenantId, Guid deviceId, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        var caNotAfter = new DateTimeOffset(_ca.NotAfter.ToUniversalTime());
        if (notAfter > caNotAfter)
        {
            notAfter = caNotAfter;
        }

        var subject = new X500DistinguishedName($"CN={deviceId:D}, OU={tenantId:D}, O=Solvix Device");
        var request = new CertificateRequest(subject, publicKey, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ClientAuthenticationOid)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(publicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_ca, true, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(DeviceIdentityUri.Format(tenantId, deviceId)));
        request.CertificateExtensions.Add(san.Build(false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive INTEGER
        var certificate = request.Create(_ca.SubjectName, X509SignatureGenerator.CreateForECDsa(_key), notBefore, notAfter, serial);
        return new IssuedDeviceCertificate(
            certificate,
            certificate.ExportCertificatePem(),
            certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(),
            certificate.SerialNumber,
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime()));
    }

    public void Dispose()
    {
        _key.Dispose();
        _ca.Dispose();
    }

    /// <summary>Creates a new self-signed device CA (P-384, 10 years).</summary>
    internal static X509Certificate2 CreateCa(string subjectName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest(subjectName, key, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
    }

    private static X509Certificate2 Load(DeviceCaOptions options, ILogger logger)
    {
        var flags = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? X509KeyStorageFlags.EphemeralKeySet : X509KeyStorageFlags.DefaultKeySet;

        if (!string.IsNullOrWhiteSpace(options.PfxBase64))
        {
            return X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(options.PfxBase64), options.PfxPassword, flags);
        }

        if (!string.IsNullOrWhiteSpace(options.PfxPath) && File.Exists(options.PfxPath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(options.PfxPath, options.PfxPassword, flags);
        }

        if (!options.AutoGenerate)
        {
            throw new InvalidOperationException(
                "No device CA is configured. Set Solvix:Devices:CertificateAuthority:PfxPath or PfxBase64 (and PfxPassword), " +
                "or enable AutoGenerate for development.");
        }

        using var generated = CreateCa(options.SubjectName);
        var pfx = generated.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, options.PfxPassword);
        if (!string.IsNullOrWhiteSpace(options.PfxPath))
        {
            Persist(options.PfxPath, pfx, logger);

            // Another instance may have won the race; always use what is on disk.
            return X509CertificateLoader.LoadPkcs12FromFile(options.PfxPath, options.PfxPassword, flags);
        }

        LogEphemeral(logger);
        return X509CertificateLoader.LoadPkcs12(pfx, options.PfxPassword, flags);
    }

    private static void Persist(string path, byte[] pfx, ILogger logger)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            stream.Write(pfx);
            LogGenerated(logger, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Created concurrently by another instance.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device CA loaded: {Subject} (sha256 {Thumbprint}, expires {NotAfter:O})")]
    private static partial void LogLoaded(ILogger logger, string subject, string thumbprint, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Generated a new device CA at {Path}. Use a managed CA bundle in production.")]
    private static partial void LogGenerated(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Generated an in-memory device CA. Device certificates become invalid when the process restarts.")]
    private static partial void LogEphemeral(ILogger logger);
}
