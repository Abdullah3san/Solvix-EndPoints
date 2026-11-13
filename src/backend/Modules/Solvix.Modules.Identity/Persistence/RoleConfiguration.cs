namespace Solvix.Modules.Identity.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Identity.Domain;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).HasMaxLength(Role.NameMaxLength).IsRequired();
        builder.Property(r => r.NormalizedName).HasMaxLength(Role.NameMaxLength).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(Role.DescriptionMaxLength);
        builder.Property(r => r.SystemKey).HasMaxLength(Role.SystemKeyMaxLength);
        builder.Ignore(r => r.IsSystem);

        builder.HasAlternateKey(r => new { r.TenantId, r.Id });

        builder.HasMany(r => r.Permissions)
            .WithOne()
            .HasForeignKey(p => new { p.TenantId, p.RoleId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Permissions).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(r => new { r.TenantId, r.NormalizedName }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.SystemKey })
            .IsUnique()
            .HasFilter("system_key IS NOT NULL");
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Permission).HasMaxLength(RolePermission.PermissionMaxLength).IsRequired();
        builder.HasIndex(p => new { p.TenantId, p.RoleId, p.Permission }).IsUnique();
    }
}
