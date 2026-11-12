namespace Solvix.Modules.Organization.Application.CompanyProfiles;

using Solvix.Modules.Organization.Domain;

public sealed record CompanyProfileDto(
    Guid Id,
    string LegalName,
    string? TradeName,
    string? TaxId,
    string? RegistrationNumber,
    string? Industry,
    string? Website,
    string? ContactEmail,
    string? ContactPhone,
    string TimeZone,
    AddressDto Address,
    DateTimeOffset UpdatedAtUtc)
{
    public static CompanyProfileDto From(CompanyProfile c) => new(
        c.Id,
        c.LegalName,
        c.TradeName,
        c.TaxId,
        c.RegistrationNumber,
        c.Industry,
        c.Website,
        c.ContactEmail,
        c.ContactPhone,
        c.TimeZone,
        AddressDto.From(c.Address),
        c.UpdatedAtUtc);
}

public sealed record UpdateCompanyProfileRequest(
    string LegalName,
    string? TradeName,
    string? TaxId,
    string? RegistrationNumber,
    string? Industry,
    string? Website,
    string? ContactEmail,
    string? ContactPhone,
    string? TimeZone,
    AddressDto? Address);
