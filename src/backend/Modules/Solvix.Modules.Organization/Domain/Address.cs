namespace Solvix.Modules.Organization.Domain;

using Solvix.SharedKernel.Guards;

/// <summary>Postal address value object, persisted as owned columns.</summary>
public sealed record Address
{
    public const int LineMaxLength = 200;
    public const int CityMaxLength = 120;
    public const int RegionMaxLength = 120;
    public const int PostalCodeMaxLength = 32;
    public const int CountryCodeLength = 2;

    public string? Line1 { get; init; }

    public string? Line2 { get; init; }

    public string? City { get; init; }

    public string? Region { get; init; }

    public string? PostalCode { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country code (upper case).</summary>
    public string? CountryCode { get; init; }

    public static Address Empty { get; } = new();

    public bool IsEmpty =>
        Line1 is null && Line2 is null && City is null && Region is null && PostalCode is null && CountryCode is null;

    public static Address Create(
        string? line1,
        string? line2,
        string? city,
        string? region,
        string? postalCode,
        string? countryCode)
    {
        var country = Guard.Optional(countryCode, "address.countryCode", CountryCodeLength)?.ToUpperInvariant();
        if (country is not null && (country.Length != CountryCodeLength || !country.All(char.IsAsciiLetterUpper)))
        {
            throw new SharedKernel.Exceptions.ValidationException("address.countryCode", "Country code must be a two-letter ISO 3166-1 alpha-2 code.");
        }

        return new Address
        {
            Line1 = Guard.Optional(line1, "address.line1", LineMaxLength),
            Line2 = Guard.Optional(line2, "address.line2", LineMaxLength),
            City = Guard.Optional(city, "address.city", CityMaxLength),
            Region = Guard.Optional(region, "address.region", RegionMaxLength),
            PostalCode = Guard.Optional(postalCode, "address.postalCode", PostalCodeMaxLength),
            CountryCode = country,
        };
    }
}
