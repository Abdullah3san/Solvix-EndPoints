namespace Solvix.Modules.Devices.Domain;

using System.Security.Cryptography;
using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A way to deploy the agent to a set of computers: the generic installer an administrator downloads from the
/// console (e.g. one per store). Devices enrolled through it are placed in its branch, department and group, but
/// only after an administrator approves them.
///
/// The enrollment code in the installer is NOT a secret credential. It identifies the profile; what keeps
/// strangers out is the approval step (and the code can be rotated or the profile disabled at any time).
/// </summary>
public sealed class DeploymentProfile : TenantEntity
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int SecretLength = 32;

    private DeploymentProfile()
    {
    }

    private DeploymentProfile(Guid tenantId, string name, string? description, Guid? branchId, Guid? departmentId, Guid? deviceGroupId, Guid createdByUserId, byte[] secret)
        : base(tenantId)
    {
        Rename(name);
        Description = description;
        BranchId = branchId;
        DepartmentId = departmentId;
        DeviceGroupId = deviceGroupId;
        CreatedByUserId = createdByUserId;
        CodeSecret = secret;
        CodeVersion = 1;
        IsEnabled = true;
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Upper-cased name; unique per tenant.</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid? DepartmentId { get; private set; }

    public Guid? DeviceGroupId { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>Secret half of the enrollment code. Rotating it invalidates every installer already downloaded.</summary>
    public byte[] CodeSecret { get; private set; } = [];

    public int CodeVersion { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset? LastRequestAtUtc { get; private set; }

    public static DeploymentProfile Create(Guid tenantId, string name, string? description, Guid? branchId, Guid? departmentId, Guid? deviceGroupId, Guid createdByUserId) =>
        new(tenantId, name, Guard.Optional(description, "description", DescriptionMaxLength), branchId, departmentId, deviceGroupId, createdByUserId, RandomNumberGenerator.GetBytes(SecretLength));

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    public void Update(string name, string? description, Guid? branchId, Guid? departmentId, Guid? deviceGroupId)
    {
        Rename(name);
        Description = Guard.Optional(description, "description", DescriptionMaxLength);
        BranchId = branchId;
        DepartmentId = departmentId;
        DeviceGroupId = deviceGroupId;
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public void RotateCode()
    {
        CodeSecret = RandomNumberGenerator.GetBytes(SecretLength);
        CodeVersion++;
    }

    public bool CodeMatches(ReadOnlySpan<byte> secret) => CryptographicOperations.FixedTimeEquals(secret, CodeSecret);

    public void RecordRequest(DateTimeOffset now) => LastRequestAtUtc = now;

    private void Rename(string name)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        NormalizedName = Normalize(Name);
    }
}
