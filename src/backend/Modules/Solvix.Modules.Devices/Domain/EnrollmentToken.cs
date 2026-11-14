namespace Solvix.Modules.Devices.Domain;

using System.Security.Cryptography;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>
/// Short-lived, use-limited secret that authorises agents to enroll into one
/// tenant. Only a SHA-256 hash of the secret is stored; the plaintext is shown
/// once at creation. Tokens are never part of the agent installer: they are
/// supplied at install time and deleted by the agent after enrollment.
/// </summary>
public sealed class EnrollmentToken : TenantEntity
{
    public const int NameMaxLength = 100;
    public const int SecretHashLength = 32;

    private EnrollmentToken()
    {
    }

    private EnrollmentToken(Guid tenantId, string name, byte[] secretHash, DateTimeOffset expiresAtUtc, int maxUses, Guid createdByUserId, Guid? deviceGroupId, Guid? branchId, Guid? departmentId)
        : base(tenantId)
    {
        Name = name;
        SecretHash = secretHash;
        ExpiresAtUtc = expiresAtUtc;
        MaxUses = maxUses;
        CreatedByUserId = createdByUserId;
        DeviceGroupId = deviceGroupId;
        BranchId = branchId;
        DepartmentId = departmentId;
    }

    public string Name { get; private set; } = string.Empty;

    public byte[] SecretHash { get; private set; } = [];

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public int MaxUses { get; private set; }

    public int UseCount { get; private set; }

    public DateTimeOffset? LastUsedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    /// <summary>Optional group every device enrolled with this token joins.</summary>
    public Guid? DeviceGroupId { get; private set; }

    /// <summary>Optional branch every device enrolled with this token is assigned to (e.g. one token per store).</summary>
    public Guid? BranchId { get; private set; }

    /// <summary>Optional department (implies its branch).</summary>
    public Guid? DepartmentId { get; private set; }

    public static EnrollmentToken Create(
        Guid tenantId,
        Guid id,
        string name,
        ReadOnlySpan<byte> secret,
        DateTimeOffset expiresAtUtc,
        int maxUses,
        Guid createdByUserId,
        Guid? deviceGroupId,
        Guid? branchId = null,
        Guid? departmentId = null)
    {
        if (maxUses < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUses));
        }

        var token = new EnrollmentToken(
            tenantId,
            Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength),
            HashSecret(secret),
            expiresAtUtc,
            maxUses,
            createdByUserId,
            deviceGroupId,
            branchId,
            departmentId)
        {
            Id = id,
        };
        return token;
    }

    public static byte[] HashSecret(ReadOnlySpan<byte> secret) => SHA256.HashData(secret);

    public bool SecretMatches(ReadOnlySpan<byte> secret) =>
        CryptographicOperations.FixedTimeEquals(HashSecret(secret), SecretHash);

    public EnrollmentTokenState StateAt(DateTimeOffset now) =>
        RevokedAtUtc is not null ? EnrollmentTokenState.Revoked
        : now >= ExpiresAtUtc ? EnrollmentTokenState.Expired
        : UseCount >= MaxUses ? EnrollmentTokenState.Exhausted
        : EnrollmentTokenState.Active;

    public void Revoke(DateTimeOffset now) => RevokedAtUtc ??= now;

    public void DetachGroup() => DeviceGroupId = null;
}

public enum EnrollmentTokenState
{
    Active = 1,
    Expired = 2,
    Exhausted = 3,
    Revoked = 4,
}
