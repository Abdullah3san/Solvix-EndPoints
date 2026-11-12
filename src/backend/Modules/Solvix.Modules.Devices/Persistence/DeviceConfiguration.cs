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
        builder.Property(d => d.DisplayName).HasMaxLength(Device.DisplayNameMaxLength);
        builder.Property(d => d.Notes).HasMaxLength(Device.NotesMaxLength);
        builder.Property(d => d.Platform).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.HardwareId).HasMaxLength(64);
        builder.Property(d => d.HardwareFingerprint).HasMaxLength(Device.FingerprintLength).IsFixedLength();
        builder.Property(d => d.SerialNumber).HasMaxLength(Device.ShortTextMaxLength);
        builder.Property(d => d.Manufacturer).HasMaxLength(Device.ShortTextMaxLength);
        builder.Property(d => d.Model).HasMaxLength(Device.ShortTextMaxLength);
        builder.Property(d => d.DomainName).HasMaxLength(Device.HostnameMaxLength);
        builder.Property(d => d.OsName).HasMaxLength(Device.ShortTextMaxLength);
        builder.Property(d => d.OsVersion).HasMaxLength(32);
        builder.Property(d => d.OsBuild).HasMaxLength(32);
        builder.Property(d => d.OsDisplayVersion).HasMaxLength(32);
        builder.Property(d => d.Architecture).HasMaxLength(16);
        builder.Property(d => d.ProcessorName).HasMaxLength(256);
        builder.Property(d => d.IpAddresses).HasColumnType("text[]").IsRequired();
        builder.Property(d => d.MacAddresses).HasColumnType("text[]").IsRequired();
        builder.Property(d => d.AgentVersion).HasMaxLength(Device.VersionMaxLength).IsRequired();
        builder.Property(d => d.HealthStatus).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.AgentHealthJson).HasColumnType("jsonb");
        builder.Ignore(d => d.IsInventoryRequested);

        // Referenced by memberships, credentials and inventory through (tenant_id, id):
        // the database itself guarantees child rows never point across tenants.
        builder.HasAlternateKey(d => new { d.TenantId, d.Id });

        // Duplicate detection: one record per physical machine and tenant.
        builder.HasIndex(d => new { d.TenantId, d.HardwareFingerprint })
            .IsUnique()
            .HasFilter("hardware_fingerprint IS NOT NULL");
        builder.HasIndex(d => new { d.TenantId, d.Hostname });
        builder.HasIndex(d => new { d.TenantId, d.AgentVersion });
        builder.HasIndex(d => new { d.TenantId, d.AssignedUserId });
    }
}

internal sealed class DeviceCredentialConfiguration : IEntityTypeConfiguration<DeviceCredential>
{
    public void Configure(EntityTypeBuilder<DeviceCredential> builder)
    {
        builder.ToTable("device_credentials");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Thumbprint).HasMaxLength(DeviceCredential.ThumbprintLength).IsFixedLength().IsRequired();
        builder.Property(c => c.SerialNumber).HasMaxLength(DeviceCredential.SerialNumberMaxLength).IsRequired();
        builder.Property(c => c.RevocationReason).HasConversion<string>().HasMaxLength(32);
        builder.Ignore(c => c.IsRevoked);

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.DeviceId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => c.Thumbprint).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.DeviceId });
    }
}

internal sealed class DeviceInventoryConfiguration : IEntityTypeConfiguration<DeviceInventory>
{
    public void Configure(EntityTypeBuilder<DeviceInventory> builder)
    {
        builder.ToTable("device_inventories");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.ContentHash).HasMaxLength(DeviceInventory.ContentHashMaxLength);
        builder.Property(i => i.ReportJson).HasColumnType("jsonb").IsRequired();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(i => new { i.TenantId, i.DeviceId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(i => new { i.TenantId, i.DeviceId }).IsUnique();
    }
}
