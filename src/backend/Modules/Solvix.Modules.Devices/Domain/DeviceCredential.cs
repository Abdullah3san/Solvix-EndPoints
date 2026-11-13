namespace Solvix.Modules.Devices.Domain;

using Solvix.SharedKernel.Entities;

public enum CredentialRevocationReason
{
    /// <summary>The device enrolled again with a new key pair.</summary>
    ReEnrolled = 1,

    /// <summary>A renewed certificate has been used; the older one is no longer needed.</summary>
    Superseded = 2,

    /// <summary>The device was retired by an administrator.</summary>
    DeviceRetired = 3,

    /// <summary>Explicit administrative revocation.</summary>
    Revoked = 4,
}

/// <summary>
/// An X.509 certificate issued to a device by the platform device CA. Only
/// public data is stored: the private key never leaves the device. A request
/// authenticates when the presented certificate chains to the device CA, is
/// within its validity period, and matches a non-revoked row here.
/// </summary>
public sealed class DeviceCredential : TenantEntity
{
    public const int ThumbprintLength = 64;
    public const int SerialNumberMaxLength = 64;

    private DeviceCredential()
    {
    }

    private DeviceCredential(Guid tenantId, Guid deviceId, string thumbprint, string serialNumber, DateTimeOffset notBefore, DateTimeOffset notAfter)
        : base(tenantId)
    {
        DeviceId = deviceId;
        Thumbprint = thumbprint;
        SerialNumber = serialNumber;
        NotBeforeUtc = notBefore;
        NotAfterUtc = notAfter;
    }

    public Guid DeviceId { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the DER certificate.</summary>
    public string Thumbprint { get; private set; } = string.Empty;

    public string SerialNumber { get; private set; } = string.Empty;

    public DateTimeOffset NotBeforeUtc { get; private set; }

    public DateTimeOffset NotAfterUtc { get; private set; }

    public DateTimeOffset? FirstUsedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public CredentialRevocationReason? RevocationReason { get; private set; }

    public bool IsRevoked => RevokedAtUtc is not null;

    public static DeviceCredential Create(Guid tenantId, Guid deviceId, string thumbprint, string serialNumber, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        new(tenantId, deviceId, thumbprint.ToLowerInvariant(), serialNumber, notBefore, notAfter);

    public bool IsValidAt(DateTimeOffset now) => !IsRevoked && now >= NotBeforeUtc && now <= NotAfterUtc;

    public void MarkUsed(DateTimeOffset now) => FirstUsedAtUtc ??= now;

    public void Revoke(CredentialRevocationReason reason, DateTimeOffset now)
    {
        if (IsRevoked)
        {
            return;
        }

        RevokedAtUtc = now;
        RevocationReason = reason;
    }
}
