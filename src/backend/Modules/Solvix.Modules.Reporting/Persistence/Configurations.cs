namespace Solvix.Modules.Reporting.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Reporting.Domain;

internal sealed class AvailabilitySnapshotConfiguration : IEntityTypeConfiguration<AvailabilitySnapshot>
{
    public void Configure(EntityTypeBuilder<AvailabilitySnapshot> builder)
    {
        builder.ToTable("availability_snapshots");
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => new { s.TenantId, s.HourUtc }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("ck_availability_snapshots_counts", "online_devices >= 0 AND online_devices <= active_devices"));
    }
}
