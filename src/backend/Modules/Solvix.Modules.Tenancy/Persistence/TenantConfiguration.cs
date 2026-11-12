namespace Solvix.Modules.Tenancy.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Tenancy.Domain;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(Tenant.NameMaxLength).IsRequired();
        builder.Property(t => t.Slug).HasMaxLength(SharedKernel.Tenancy.Slug.MaxLength).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.HasIndex(t => t.Slug).IsUnique();
    }
}
