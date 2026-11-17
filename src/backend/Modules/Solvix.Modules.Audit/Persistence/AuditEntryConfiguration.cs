namespace Solvix.Modules.Audit.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Audit.Domain;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.ActorType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.ActorName).HasMaxLength(AuditEntry.ActorNameMaxLength);
        builder.Property(a => a.Action).HasMaxLength(AuditEntry.ActionMaxLength).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(AuditEntry.EntityTypeMaxLength).IsRequired();
        builder.Property(a => a.Summary).HasMaxLength(AuditEntry.SummaryMaxLength).IsRequired();
        builder.Property(a => a.DetailsJson).HasColumnType("jsonb");
        builder.Property(a => a.CorrelationId).HasMaxLength(AuditEntry.CorrelationIdMaxLength);

        builder.HasIndex(a => new { a.TenantId, a.OccurredAtUtc });
        builder.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId });
        builder.HasIndex(a => new { a.TenantId, a.Action });
    }
}
