namespace Solvix.Modules.Organization.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>
/// A physical or logical site of the tenant's organisation. Codes are unique per
/// tenant and at most one branch is flagged as headquarters.
/// </summary>
public sealed class Branch : TenantEntity
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 32;
    public const int PhoneMaxLength = 40;
    public const int EmailMaxLength = 320;
    public const int TimeZoneMaxLength = 64;

    private Branch()
    {
    }

    private Branch(Guid tenantId, string name, string code)
        : base(tenantId)
    {
        Name = name;
        Code = code;
        IsActive = true;
        Address = Address.Empty;
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Short, upper-case identifier such as "HQ" or "NYC-01".</summary>
    public string Code { get; private set; } = string.Empty;

    public bool IsHeadquarters { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? TimeZone { get; private set; }

    public bool IsActive { get; private set; }

    public Address Address { get; private set; } = Address.Empty;

    public static Branch Create(Guid tenantId, string name, string code) =>
        new(tenantId, Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength), NormalizeCode(code));

    public static string NormalizeCode(string code) =>
        Guard.NotNullOrWhiteSpace(code, "code", CodeMaxLength).ToUpperInvariant();

    public void Update(string name, string code, string? phone, string? email, string? timeZone, Address address)
    {
        Name = Guard.NotNullOrWhiteSpace(name, "name", NameMaxLength);
        Code = NormalizeCode(code);
        Phone = Guard.Optional(phone, "phone", PhoneMaxLength);
        Email = Guard.Optional(email, "email", EmailMaxLength);
        TimeZone = Guard.Optional(timeZone, "timeZone", TimeZoneMaxLength);
        Address = address ?? Address.Empty;
    }

    public void MarkAsHeadquarters() => IsHeadquarters = true;

    public void UnmarkHeadquarters() => IsHeadquarters = false;

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
