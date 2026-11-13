namespace Solvix.Modules.Devices.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A secret an administrator hands to the agent installer (e.g. through an MSI
/// property or Intune) so devices can enrol into the tenant. Only the hash is
/// stored. Tokens expire, can be limited in uses and revoked; devices enrolled
/// with a token inherit its default branch, department and group.
/// </summary>
public sealed class EnrollmentToken : TenantEntity
{
    public const int NameMaxLength = 200;

    private EnrollmentToken()
    {
    }

    private EnrollmentToken(Guid tenantId, string name, string secretHash, DateTimeOffset expiresAtUtc, int? maxUses, Guid createdByUserId)
        : base(tenantId)
    {
        Name = name;
        SecretHash = secretHash;
        ExpiresAtUtc = expiresAtUtc;
        MaxUses = maxUses;
        CreatedByUserId = createdByUserId;
    }

    public string Name { get; private set; } = string.Empty;

    public string SecretHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public int? MaxUses { get; private set; }

    public int UseCount { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Guid? DefaultBranchId { get; private set; }

    public Guid? DefaultDepartmentId { get; private set; }

    public Guid? DefaultDeviceGroupId { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public static EnrollmentToken Create(
        Guid tenantId,
        string name,
        string secretHash,
        DateTimeOffset expiresAtUtc,
        int? maxUses,
        Guid createdByUserId,
        Guid? branchId,
        Guid? departmentId,
        Guid? deviceGroupId) =>
        new(tenantId, Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength), secretHash, expiresAtUtc, maxUses, createdByUserId)
        {
            DefaultBranchId = branchId,
            DefaultDepartmentId = departmentId,
            DefaultDeviceGroupId = deviceGroupId,
        };

    /// <summary>The plaintext token embeds the row id, so the hash can only be computed after the id exists.</summary>
    public void AttachSecretHash(string secretHash) => SecretHash = Guard.NotNullOrWhiteSpace(secretHash, "secretHash", 128);

    public bool IsUsable(DateTimeOffset now) =>
        RevokedAtUtc is null && now < ExpiresAtUtc && (MaxUses is null || UseCount < MaxUses);

    public void Revoke(DateTimeOffset now) => RevokedAtUtc ??= now;
}
