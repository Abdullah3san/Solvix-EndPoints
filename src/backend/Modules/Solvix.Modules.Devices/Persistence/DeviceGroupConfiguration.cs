namespace Solvix.Modules.Devices.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Devices.Domain;

internal sealed class DeviceGroupConfiguration : IEntityTypeConfiguration<DeviceGroup>
{
    public void Configure(EntityTypeBuilder<DeviceGroup> builder)
    {
        builder.ToTable("device_groups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Name).HasMaxLength(DeviceGroup.NameMaxLength).IsRequired();
        builder.Property(g => g.NormalizedName).HasMaxLength(DeviceGroup.NameMaxLength).IsRequired();
        builder.Property(g => g.Description).HasMaxLength(DeviceGroup.DescriptionMaxLength);

        builder.HasAlternateKey(g => new { g.TenantId, g.Id });
        builder.HasIndex(g => new { g.TenantId, g.NormalizedName }).IsUnique();
    }
}

internal sealed class DeviceGroupMembershipConfiguration : IEntityTypeConfiguration<DeviceGroupMembership>
{
    public void Configure(EntityTypeBuilder<DeviceGroupMembership> builder)
    {
        builder.ToTable("device_group_memberships");
        builder.HasKey(m => m.Id);

        builder.HasOne<DeviceGroup>()
            .WithMany()
            .HasForeignKey(m => new { m.TenantId, m.DeviceGroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(m => new { m.TenantId, m.DeviceId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => new { m.TenantId, m.DeviceGroupId, m.DeviceId }).IsUnique();
        builder.HasIndex(m => new { m.TenantId, m.DeviceId });
    }
}

internal sealed class EnrollmentTokenConfiguration : IEntityTypeConfiguration<EnrollmentToken>
{
    public void Configure(EntityTypeBuilder<EnrollmentToken> builder)
    {
        builder.ToTable("enrollment_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(EnrollmentToken.NameMaxLength).IsRequired();
        builder.Property(t => t.SecretHash).HasMaxLength(EnrollmentToken.SecretHashLength).IsRequired();

        // Restrict: deleting a group detaches its tokens explicitly first (a composite
        // SET NULL would also null tenant_id).
        builder.HasOne<DeviceGroup>()
            .WithMany()
            .HasForeignKey(t => new { t.TenantId, t.DeviceGroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => new { t.TenantId, t.ExpiresAtUtc });
    }
}
