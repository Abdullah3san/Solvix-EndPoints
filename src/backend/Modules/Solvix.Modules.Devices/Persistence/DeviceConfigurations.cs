namespace Solvix.Modules.Devices.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Devices.Domain;

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("devices");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Hostname).HasMaxLength(Device.HostnameMaxLength).IsRequired();
        builder.Property(d => d.DnsDomain).HasMaxLength(Device.HostnameMaxLength);
        builder.Property(d => d.HardwareId).HasMaxLength(Device.ShortTextMaxLength);
        builder.Property(d => d.OsName).HasMaxLength(Device.ShortTextMaxLength).IsRequired();
        builder.Property(d => d.OsVersion).HasMaxLength(Device.ShortTextMaxLength).IsRequired();
        builder.Property(d => d.OsBuild).HasMaxLength(Device.ShortTextMaxLength).IsRequired();
        builder.Property(d => d.AgentVersion).HasMaxLength(Device.AgentVersionMaxLength).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.CredentialHash).HasMaxLength(128).IsRequired();
        builder.Ignore(d => d.IsActive);

        builder.HasAlternateKey(d => new { d.TenantId, d.Id });

        builder.HasOne(d => d.Branch)
            .WithMany()
            .HasForeignKey(d => new { d.TenantId, d.BranchId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.Department)
            .WithMany()
            .HasForeignKey(d => new { d.TenantId, d.DepartmentId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => new { d.TenantId, d.HardwareId })
            .IsUnique()
            .HasFilter("hardware_id IS NOT NULL");
        builder.HasIndex(d => new { d.TenantId, d.Hostname });
        builder.HasIndex(d => new { d.TenantId, d.BranchId });
        builder.HasIndex(d => new { d.TenantId, d.DepartmentId });
    }
}

internal sealed class DeviceGroupConfiguration : IEntityTypeConfiguration<DeviceGroup>
{
    public void Configure(EntityTypeBuilder<DeviceGroup> builder)
    {
        builder.ToTable("device_groups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Name).HasMaxLength(DeviceGroup.NameMaxLength).IsRequired();
        builder.Property(g => g.Description).HasMaxLength(DeviceGroup.DescriptionMaxLength);
        builder.HasAlternateKey(g => new { g.TenantId, g.Id });
        builder.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
    }
}

internal sealed class DeviceGroupMemberConfiguration : IEntityTypeConfiguration<DeviceGroupMember>
{
    public void Configure(EntityTypeBuilder<DeviceGroupMember> builder)
    {
        builder.ToTable("device_group_members");
        builder.HasKey(m => m.Id);

        builder.HasOne(m => m.DeviceGroup)
            .WithMany()
            .HasForeignKey(m => new { m.TenantId, m.DeviceGroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Device)
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
        builder.Property(t => t.SecretHash).HasMaxLength(128).IsRequired();
        builder.HasIndex(t => new { t.TenantId, t.CreatedAtUtc });

        builder.HasOne<Solvix.Modules.Organization.Domain.Branch>()
            .WithMany()
            .HasForeignKey(t => new { t.TenantId, t.DefaultBranchId })
            .HasPrincipalKey(b => new { b.TenantId, b.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Solvix.Modules.Organization.Domain.Department>()
            .WithMany()
            .HasForeignKey(t => new { t.TenantId, t.DefaultDepartmentId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<DeviceGroup>()
            .WithMany()
            .HasForeignKey(t => new { t.TenantId, t.DefaultDeviceGroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
