namespace Solvix.Modules.Identity.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Identity.Domain;

internal sealed class TenantSecurityPolicyConfiguration : IEntityTypeConfiguration<TenantSecurityPolicy>
{
    public void Configure(EntityTypeBuilder<TenantSecurityPolicy> builder)
    {
        builder.ToTable("tenant_security_policies");
        builder.HasKey(p => p.Id);
        builder.HasIndex(p => p.TenantId).IsUnique();
    }
}

internal sealed class AuthAuditEventConfiguration : IEntityTypeConfiguration<AuthAuditEvent>
{
    public void Configure(EntityTypeBuilder<AuthAuditEvent> builder)
    {
        builder.ToTable("auth_audit_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.EventType).HasMaxLength(AuthAuditEvent.EventTypeMaxLength).IsRequired();
        builder.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.Email).HasMaxLength(AuthAuditEvent.EmailMaxLength);
        builder.Property(e => e.IpAddress).HasMaxLength(AuthAuditEvent.IpAddressMaxLength);
        builder.Property(e => e.UserAgent).HasMaxLength(AuthAuditEvent.UserAgentMaxLength);
        builder.Property(e => e.CorrelationId).HasMaxLength(AuthAuditEvent.CorrelationIdMaxLength);
        builder.Property(e => e.Detail).HasMaxLength(AuthAuditEvent.DetailMaxLength);

        builder.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.UserId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.EventType, e.OccurredAtUtc });
    }
}
