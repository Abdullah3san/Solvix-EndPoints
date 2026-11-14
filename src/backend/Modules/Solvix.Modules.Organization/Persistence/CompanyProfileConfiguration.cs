namespace Solvix.Modules.Organization.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Organization.Domain;

internal sealed class CompanyProfileConfiguration : IEntityTypeConfiguration<CompanyProfile>
{
    public void Configure(EntityTypeBuilder<CompanyProfile> builder)
    {
        builder.ToTable("company_profiles");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.LegalName).HasMaxLength(CompanyProfile.NameMaxLength).IsRequired();
        builder.Property(c => c.TradeName).HasMaxLength(CompanyProfile.NameMaxLength);
        builder.Property(c => c.TaxId).HasMaxLength(CompanyProfile.TaxIdMaxLength);
        builder.Property(c => c.RegistrationNumber).HasMaxLength(CompanyProfile.TaxIdMaxLength);
        builder.Property(c => c.Industry).HasMaxLength(CompanyProfile.IndustryMaxLength);
        builder.Property(c => c.Website).HasMaxLength(CompanyProfile.WebsiteMaxLength);
        builder.Property(c => c.ContactEmail).HasMaxLength(CompanyProfile.EmailMaxLength);
        builder.Property(c => c.ContactPhone).HasMaxLength(CompanyProfile.PhoneMaxLength);
        builder.Property(c => c.TimeZone).HasMaxLength(CompanyProfile.TimeZoneMaxLength).IsRequired();
        builder.OwnsOne(c => c.Address, a => a.ConfigureAddress());
        builder.Navigation(c => c.Address).IsRequired();

        // One profile per tenant.
        builder.HasIndex(c => c.TenantId).IsUnique();
    }
}
