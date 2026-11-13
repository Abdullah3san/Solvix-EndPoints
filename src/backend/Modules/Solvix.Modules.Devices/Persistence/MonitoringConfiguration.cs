namespace Solvix.Modules.Devices.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Monitoring;

internal sealed class DevicePresenceConfiguration : IEntityTypeConfiguration<DevicePresence>
{
    public void Configure(EntityTypeBuilder<DevicePresence> builder)
    {
        builder.ToTable("device_presence");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.NetworkStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.OsHealthStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.NetworkJson).HasColumnType("jsonb");
        builder.Property(p => p.OsHealthJson).HasColumnType("jsonb");

        // Transitions use optimistic concurrency. LastSeenAtUtc is a token too, so a
        // decision based on a contact time that the batched flush has since advanced
        // is retried with fresh data instead of declaring a live device silent.
        builder.Property(p => p.Revision).IsConcurrencyToken();
        builder.Property(p => p.LastSeenAtUtc).IsConcurrencyToken();

        builder.HasOne<Device>()
            .WithOne()
            .HasForeignKey<DevicePresence>(p => new { p.TenantId, p.DeviceId })
            .HasPrincipalKey<Device>(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Deliberately no index on last_seen_at_utc: contact writes stay HOT updates.
        builder.HasIndex(p => new { p.TenantId, p.State });
        builder.HasIndex(p => new { p.TenantId, p.DeviceId }).IsUnique();
    }
}

internal sealed class DeviceConnectionEventConfiguration : IEntityTypeConfiguration<DeviceConnectionEvent>
{
    public void Configure(EntityTypeBuilder<DeviceConnectionEvent> builder)
    {
        builder.ToTable("device_connection_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(e => e.Signal).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.FromStatus).HasMaxLength(16);
        builder.Property(e => e.ToStatus).HasMaxLength(16);
        builder.Property(e => e.Detail).HasMaxLength(DeviceConnectionEvent.DetailMaxLength);

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.DeviceId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.DeviceId, e.OccurredAtUtc });
    }
}

internal sealed class DeviceAvailabilityDayConfiguration : IEntityTypeConfiguration<DeviceAvailabilityDay>
{
    public void Configure(EntityTypeBuilder<DeviceAvailabilityDay> builder)
    {
        builder.ToTable("device_availability_days");
        builder.HasKey(a => a.Id);
        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.DeviceId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => new { a.TenantId, a.DeviceId, a.Day }).IsUnique();
        builder.HasIndex(a => new { a.TenantId, a.Day });
    }
}

internal sealed class MonitoringAlertConfiguration : IEntityTypeConfiguration<MonitoringAlert>
{
    public void Configure(EntityTypeBuilder<MonitoringAlert> builder)
    {
        builder.ToTable("monitoring_alerts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.DeviceName).HasMaxLength(255).IsRequired();
        builder.Property(a => a.Message).HasMaxLength(500).IsRequired();
        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.DeviceId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.TenantId, a.Status, a.OpenedAtUtc });

        // At most one open offline alert per device: duplicates are impossible even across nodes.
        builder.HasIndex(a => new { a.TenantId, a.DeviceId, a.Type })
            .IsUnique()
            .HasFilter("status = 'Open'")
            .HasDatabaseName("ux_monitoring_alerts_open_per_device");
    }
}

internal sealed class MonitoringSettingsConfiguration : IEntityTypeConfiguration<MonitoringSettings>
{
    public void Configure(EntityTypeBuilder<MonitoringSettings> builder)
    {
        builder.ToTable("monitoring_settings");
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => s.TenantId).IsUnique();
    }
}

internal sealed class MonitorCheckpointConfiguration : IEntityTypeConfiguration<MonitorCheckpoint>
{
    public void Configure(EntityTypeBuilder<MonitorCheckpoint> builder)
    {
        builder.ToTable("monitor_checkpoints");
        builder.HasKey(c => c.Name);
        builder.Property(c => c.Name).HasMaxLength(64);
    }
}
