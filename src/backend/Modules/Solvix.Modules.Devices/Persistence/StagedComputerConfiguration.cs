namespace Solvix.Modules.Devices.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Devices.Domain;

internal sealed class StagedComputerConfiguration : IEntityTypeConfiguration<StagedComputer>
{
    public void Configure(EntityTypeBuilder<StagedComputer> builder)
    {
        builder.ToTable("staged_computers");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Hostname).HasMaxLength(StagedComputer.HostnameMaxLength).IsRequired();
        builder.Property(s => s.NormalizedHostname).HasMaxLength(StagedComputer.HostnameMaxLength).IsRequired();
        builder.Property(s => s.Source).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.HasIndex(s => new { s.TenantId, s.NormalizedHostname }).IsUnique();

        // BranchId / DepartmentId reference the Organization module; their tenant-safe composite
        // foreign keys (ON DELETE SET NULL) are declared in the StagedComputers migration.
    }
}
