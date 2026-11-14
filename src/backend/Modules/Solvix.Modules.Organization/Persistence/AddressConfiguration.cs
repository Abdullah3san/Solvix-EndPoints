namespace Solvix.Modules.Organization.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Organization.Domain;

internal static class AddressConfiguration
{
    public static void ConfigureAddress<TOwner>(this OwnedNavigationBuilder<TOwner, Address> builder)
        where TOwner : class
    {
        builder.Property(a => a.Line1).HasColumnName("address_line1").HasMaxLength(Address.LineMaxLength);
        builder.Property(a => a.Line2).HasColumnName("address_line2").HasMaxLength(Address.LineMaxLength);
        builder.Property(a => a.City).HasColumnName("address_city").HasMaxLength(Address.CityMaxLength);
        builder.Property(a => a.Region).HasColumnName("address_region").HasMaxLength(Address.RegionMaxLength);
        builder.Property(a => a.PostalCode).HasColumnName("address_postal_code").HasMaxLength(Address.PostalCodeMaxLength);
        builder.Property(a => a.CountryCode).HasColumnName("address_country_code").HasMaxLength(Address.CountryCodeLength).IsFixedLength();
    }
}
