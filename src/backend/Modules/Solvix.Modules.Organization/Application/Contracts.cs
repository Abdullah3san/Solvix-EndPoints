namespace Solvix.Modules.Organization.Application;

using Solvix.Modules.Organization.Domain;

public sealed record AddressDto(
    string? Line1,
    string? Line2,
    string? City,
    string? Region,
    string? PostalCode,
    string? CountryCode)
{
    public static AddressDto From(Address a) =>
        new(a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.CountryCode);

    public Address ToDomain() => Address.Create(Line1, Line2, City, Region, PostalCode, CountryCode);
}
