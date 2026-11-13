namespace Solvix.Modules.Mdm.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// An invitation to enroll devices of one scenario under one policy. The secret
/// itself is returned exactly once at creation; only a hash is stored.
/// </summary>
public sealed class EnrollmentToken : TenantEntity
{
    public const int NameMaxLength = 200;

    private EnrollmentToken()
    {
    }

    private EnrollmentToken(Guid tenantId, ManagementScenario scenario, string name, Guid policyId, Guid createdBy)
        : base(tenantId)
    {
        Platform = scenario.Platform;
        Mode = scenario.Mode;
        Ownership = scenario.Ownership;
        Name = name;
        PolicyId = policyId;
        CreatedByUserId = createdBy;
        Method = scenario.DefaultEnrollmentMethod;
    }

    public string Name { get; private set; } = string.Empty;

    public MobilePlatform Platform { get; private set; }

    public ManagementMode Mode { get; private set; }

    public DeviceOwnership Ownership { get; private set; }

    public EnrollmentMethod Method { get; private set; }

    public Guid PolicyId { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    /// <summary>SHA-256 (hex) of the secret presented by the device or user. Null for provider-issued tokens.</summary>
    public string? SecretHash { get; private set; }

    /// <summary>Android: enrollment token resource name issued by the Android Management API.</summary>
    public string? ProviderReference { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public int MaxUses { get; private set; }

    public int UseCount { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public ManagementScenario Scenario => new(Platform, Mode, Ownership);

    public bool IsUsable(DateTimeOffset now) => RevokedAtUtc is null && now < ExpiresAtUtc && UseCount < MaxUses;

    public static EnrollmentToken Create(
        Guid tenantId,
        ManagementScenario scenario,
        string name,
        Guid policyId,
        Guid createdBy,
        DateTimeOffset expiresAtUtc,
        int maxUses,
        string? secretHash)
    {
        scenario.EnsureValid();
        if (maxUses < 1 || maxUses > 10_000)
        {
            throw new ValidationException("maxUses", "maxUses must be between 1 and 10000.");
        }

        return new EnrollmentToken(tenantId, scenario, Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength), policyId, createdBy)
        {
            ExpiresAtUtc = expiresAtUtc,
            MaxUses = maxUses,
            SecretHash = secretHash,
        };
    }

    public void AttachProviderReference(string reference) => ProviderReference = reference;

    public void RegisterUse(DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            throw new ConflictException("The enrollment token is expired, revoked or has no uses left.");
        }

        UseCount++;
    }

    public void Revoke(DateTimeOffset now) => RevokedAtUtc ??= now;
}
