namespace Solvix.Modules.Organization.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>
/// Business-facing details of the organisation behind a tenant. Exactly one
/// profile exists per tenant (enforced by a unique index on tenant_id).
/// Account-level data (slug, status) stays on the tenant itself to avoid
/// duplication.
/// </summary>
public sealed class CompanyProfile : TenantEntity
{
    public const int NameMaxLength = 200;
    public const int TaxIdMaxLength = 64;
    public const int IndustryMaxLength = 120;
    public const int WebsiteMaxLength = 512;
    public const int EmailMaxLength = 320;
    public const int PhoneMaxLength = 40;
    public const int TimeZoneMaxLength = 64;

    private CompanyProfile()
    {
    }

    private CompanyProfile(Guid tenantId, string legalName)
        : base(tenantId)
    {
        LegalName = legalName;
        TimeZone = "UTC";
        Address = Address.Empty;
    }

    public string LegalName { get; private set; } = string.Empty;

    public string? TradeName { get; private set; }

    public string? TaxId { get; private set; }

    public string? RegistrationNumber { get; private set; }

    public string? Industry { get; private set; }

    public string? Website { get; private set; }

    public string? ContactEmail { get; private set; }

    public string? ContactPhone { get; private set; }

    /// <summary>IANA time zone id used as the tenant default.</summary>
    public string TimeZone { get; private set; } = "UTC";

    public Address Address { get; private set; } = Address.Empty;

    public static CompanyProfile Create(Guid tenantId, string legalName) =>
        new(tenantId, Guard.NotNullOrWhiteSpace(legalName, "legalName", NameMaxLength));

    public void Update(
        string legalName,
        string? tradeName,
        string? taxId,
        string? registrationNumber,
        string? industry,
        string? website,
        string? contactEmail,
        string? contactPhone,
        string timeZone,
        Address address)
    {
        LegalName = Guard.NotNullOrWhiteSpace(legalName, "legalName", NameMaxLength);
        TradeName = Guard.Optional(tradeName, "tradeName", NameMaxLength);
        TaxId = Guard.Optional(taxId, "taxId", TaxIdMaxLength);
        RegistrationNumber = Guard.Optional(registrationNumber, "registrationNumber", TaxIdMaxLength);
        Industry = Guard.Optional(industry, "industry", IndustryMaxLength);
        Website = Guard.Optional(website, "website", WebsiteMaxLength);
        ContactEmail = Guard.Optional(contactEmail, "contactEmail", EmailMaxLength);
        ContactPhone = Guard.Optional(contactPhone, "contactPhone", PhoneMaxLength);
        TimeZone = Guard.NotNullOrWhiteSpace(timeZone, "timeZone", TimeZoneMaxLength);
        Address = address ?? Address.Empty;
    }
}
