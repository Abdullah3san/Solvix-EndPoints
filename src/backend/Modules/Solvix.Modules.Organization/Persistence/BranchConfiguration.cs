namespace Solvix.Modules.Organization.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Organization.Domain;

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("branches");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Name).HasMaxLength(Branch.NameMaxLength).IsRequired();
        builder.Property(b => b.Code).HasMaxLength(Branch.CodeMaxLength).IsRequired();
        builder.Property(b => b.Phone).HasMaxLength(Branch.PhoneMaxLength);
        builder.Property(b => b.Email).HasMaxLength(Branch.EmailMaxLength);
        builder.Property(b => b.TimeZone).HasMaxLength(Branch.TimeZoneMaxLength);
        builder.Property(b => b.IsActive).IsRequired();
        builder.Property(b => b.IsHeadquarters).IsRequired();
        builder.OwnsOne(b => b.Address, a => a.ConfigureAddress());
        builder.Navigation(b => b.Address).IsRequired();

        builder.HasIndex(b => new { b.TenantId, b.Code }).IsUnique();

        // Composite alternate key so child tables can reference (tenant_id, id)
        // and the database itself guarantees children never cross tenants.
        builder.HasAlternateKey(b => new { b.TenantId, b.Id });

        // At most one headquarters per tenant.
        builder.HasIndex(b => b.TenantId)
            .HasDatabaseName("ix_branches_tenant_headquarters")
            .IsUnique()
            .HasFilter("is_headquarters = TRUE");
    }
}
