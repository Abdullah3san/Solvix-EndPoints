namespace Solvix.Modules.Devices.Security;

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Solvix.Agent.Contracts;

/// <summary>Reads the <c>urn:solvix:device:{tenant}:{device}</c> identity from a device certificate.</summary>
public static class DeviceCertificateIdentity
{
    private const string SubjectAltNameOid = "2.5.29.17";
    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);

    public static bool TryRead(X509Certificate2 certificate, out Guid tenantId, out Guid deviceId)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        tenantId = Guid.Empty;
        deviceId = Guid.Empty;

        var extension = certificate.Extensions[SubjectAltNameOid];
        if (extension is null)
        {
            return false;
        }

        var found = 0;
        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
            var names = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            while (names.HasData)
            {
                var tag = names.PeekTag();
                if (tag.HasSameClassAndValue(UriTag))
                {
                    var uri = names.ReadCharacterString(UniversalTagNumber.IA5String, UriTag);
                    if (DeviceIdentityUri.TryParse(uri, out var t, out var d))
                    {
                        // Exactly one device identity is allowed per certificate.
                        if (++found > 1)
                        {
                            return false;
                        }

                        tenantId = t;
                        deviceId = d;
                    }
                }
                else
                {
                    names.ReadEncodedValue();
                }
            }
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException)
        {
            return false;
        }

        if (found != 1)
        {
            tenantId = Guid.Empty;
            deviceId = Guid.Empty;
            return false;
        }

        // The subject CN must agree with the SAN.
        var cn = certificate.GetNameInfo(X509NameType.SimpleName, false);
        return Guid.TryParse(cn, out var cnDevice) && cnDevice == deviceId;
    }
}
