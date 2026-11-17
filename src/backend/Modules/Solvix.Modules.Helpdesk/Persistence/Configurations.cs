namespace Solvix.Modules.Helpdesk.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Helpdesk.Domain;

internal sealed class TicketCategoryConfiguration : IEntityTypeConfiguration<TicketCategory>
{
    public void Configure(EntityTypeBuilder<TicketCategory> builder)
    {
        builder.ToTable("ticket_categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name).HasMaxLength(TicketCategory.NameMaxLength).IsRequired();
        builder.Property(c => c.NormalizedName).HasMaxLength(TicketCategory.NameMaxLength).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(500);
        builder.Property(c => c.Keywords).HasColumnType("text[]").IsRequired();
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });
        builder.HasIndex(c => new { c.TenantId, c.NormalizedName }).IsUnique();
    }
}

internal sealed class TechnicianConfiguration : IEntityTypeConfiguration<Technician>
{
    public void Configure(EntityTypeBuilder<Technician> builder)
    {
        builder.ToTable("technicians");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.CategoryIds).HasColumnType("uuid[]").IsRequired();
        builder.Property(t => t.Notes).HasMaxLength(500);
        builder.HasAlternateKey(t => new { t.TenantId, t.Id });
        builder.HasIndex(t => new { t.TenantId, t.UserId }).IsUnique();
    }
}

internal sealed class SlaPolicyConfiguration : IEntityTypeConfiguration<SlaPolicy>
{
    public void Configure(EntityTypeBuilder<SlaPolicy> builder)
    {
        builder.ToTable("sla_policies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).HasMaxLength(100).IsRequired();
        builder.Property(p => p.NormalizedName).HasMaxLength(100).IsRequired();
        builder.Property(p => p.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.HasAlternateKey(p => new { p.TenantId, p.Id });
        builder.HasIndex(p => new { p.TenantId, p.NormalizedName }).IsUnique();
        // Exactly one default policy per tenant, enforced by the database.
        builder.HasIndex(p => p.TenantId).IsUnique().HasFilter("is_default").HasDatabaseName("ux_sla_policies_one_default");
        builder.HasMany(p => p.Targets).WithOne().HasForeignKey(t => new { t.TenantId, t.SlaPolicyId }).HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Targets).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class SlaTargetConfiguration : IEntityTypeConfiguration<SlaTarget>
{
    public void Configure(EntityTypeBuilder<SlaTarget> builder)
    {
        builder.ToTable("sla_targets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasIndex(t => new { t.TenantId, t.SlaPolicyId, t.Priority }).IsUnique();
    }
}

internal sealed class EscalationRuleConfiguration : IEntityTypeConfiguration<EscalationRule>
{
    public void Configure(EntityTypeBuilder<EscalationRule> builder)
    {
        builder.ToTable("escalation_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.Action).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.MinPriority).HasConversion<string>().HasMaxLength(16);
        builder.HasAlternateKey(r => new { r.TenantId, r.Id });
        builder.HasIndex(r => new { r.TenantId, r.IsActive });
    }
}

internal sealed class TicketCounterConfiguration : IEntityTypeConfiguration<TicketCounter>
{
    public void Configure(EntityTypeBuilder<TicketCounter> builder)
    {
        builder.ToTable("ticket_counters");
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => c.TenantId).IsUnique();
    }
}

internal sealed class InboundEmailChannelConfiguration : IEntityTypeConfiguration<InboundEmailChannel>
{
    public void Configure(EntityTypeBuilder<InboundEmailChannel> builder)
    {
        builder.ToTable("inbound_email_channels");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Address).HasMaxLength(254);
        builder.Property(c => c.SecretHash).HasMaxLength(InboundEmailChannel.SecretHashLength).IsFixedLength().IsRequired();
        builder.Property(c => c.DefaultPriority).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.AllowedSenderDomains).HasColumnType("text[]").IsRequired();
        builder.HasIndex(c => c.TenantId);
    }
}

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Subject).HasMaxLength(Ticket.SubjectMaxLength).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(Ticket.DescriptionMaxLength).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.Source).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.RequesterEmail).HasMaxLength(254);
        builder.Property(t => t.RequesterName).HasMaxLength(200);
        builder.Property(t => t.ExternalMessageId).HasMaxLength(512);
        builder.Property(t => t.Summary).HasMaxLength(4000);
        builder.Property(t => t.SummarySource).HasMaxLength(32);
        builder.Ignore(t => t.NeedsTriage);
        builder.Ignore(t => t.IsOpen);
        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasAlternateKey(t => new { t.TenantId, t.Id });
        builder.HasIndex(t => new { t.TenantId, t.Number }).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.Status, t.Priority });
        builder.HasIndex(t => new { t.TenantId, t.AssigneeTechnicianId, t.Status });
        builder.HasIndex(t => new { t.TenantId, t.RequesterUserId });
        builder.HasIndex(t => new { t.TenantId, t.LastActivityAtUtc });
        builder.HasIndex(t => new { t.TenantId, t.ExternalMessageId }).IsUnique().HasFilter("external_message_id IS NOT NULL");

        // Same-tenant composite foreign keys: a ticket can never point at another tenant's category/technician/policy.
        builder.HasOne<TicketCategory>().WithMany().HasForeignKey(t => new { t.TenantId, t.CategoryId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Technician>().WithMany().HasForeignKey(t => new { t.TenantId, t.AssigneeTechnicianId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SlaPolicy>().WithMany().HasForeignKey(t => new { t.TenantId, t.SlaPolicyId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);

        // device_id references the Devices module; its tenant-safe FK is declared in the HelpdeskSecurity migration.
    }
}

internal sealed class TicketCommentConfiguration : IEntityTypeConfiguration<TicketComment>
{
    public void Configure(EntityTypeBuilder<TicketComment> builder)
    {
        builder.ToTable("ticket_comments");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Body).HasMaxLength(TicketComment.BodyMaxLength).IsRequired();
        builder.Property(c => c.AuthorName).HasMaxLength(200);
        builder.Property(c => c.AuthorEmail).HasMaxLength(254);
        builder.Property(c => c.Source).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.ExternalMessageId).HasMaxLength(512);
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });
        builder.HasOne<Ticket>().WithMany().HasForeignKey(c => new { c.TenantId, c.TicketId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.TenantId, c.TicketId, c.CreatedAtUtc });
        builder.HasIndex(c => new { c.TenantId, c.ExternalMessageId }).IsUnique().HasFilter("external_message_id IS NOT NULL");
    }
}

internal sealed class TicketAttachmentConfiguration : IEntityTypeConfiguration<TicketAttachment>
{
    public void Configure(EntityTypeBuilder<TicketAttachment> builder)
    {
        builder.ToTable("ticket_attachments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.FileName).HasMaxLength(255).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.HasAlternateKey(a => new { a.TenantId, a.Id });
        builder.HasOne<Ticket>().WithMany().HasForeignKey(a => new { a.TenantId, a.TicketId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => new { a.TenantId, a.TicketId });
    }
}

internal sealed class TicketAttachmentContentConfiguration : IEntityTypeConfiguration<TicketAttachmentContent>
{
    public void Configure(EntityTypeBuilder<TicketAttachmentContent> builder)
    {
        builder.ToTable("ticket_attachment_contents");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Data).HasColumnType("bytea").IsRequired();
        builder.HasOne<TicketAttachment>().WithMany().HasForeignKey(c => new { c.TenantId, c.AttachmentId }).HasPrincipalKey(a => new { a.TenantId, a.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.TenantId, c.AttachmentId }).IsUnique();
    }
}

internal sealed class TicketEventConfiguration : IEntityTypeConfiguration<TicketEvent>
{
    public void Configure(EntityTypeBuilder<TicketEvent> builder)
    {
        builder.ToTable("ticket_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(e => e.ActorLabel).HasMaxLength(200);
        builder.Property(e => e.FromValue).HasMaxLength(200);
        builder.Property(e => e.ToValue).HasMaxLength(200);
        builder.Property(e => e.Detail).HasMaxLength(2000);
        builder.HasOne<Ticket>().WithMany().HasForeignKey(e => new { e.TenantId, e.TicketId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => new { e.TenantId, e.TicketId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.Type, e.OccurredAtUtc });
    }
}

internal sealed class TicketResolutionConfiguration : IEntityTypeConfiguration<TicketResolution>
{
    public void Configure(EntityTypeBuilder<TicketResolution> builder)
    {
        builder.ToTable("ticket_resolutions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Notes).HasMaxLength(5000).IsRequired();
        builder.HasOne<Ticket>().WithMany().HasForeignKey(r => new { r.TenantId, r.TicketId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.TenantId, r.TicketId, r.ResolvedAtUtc });
    }
}

/// <summary>Escalations already applied to a ticket (one row per rule): makes the escalation job idempotent.</summary>
internal sealed class AppliedEscalationConfiguration : IEntityTypeConfiguration<AppliedEscalation>
{
    public void Configure(EntityTypeBuilder<AppliedEscalation> builder)
    {
        builder.ToTable("ticket_applied_escalations");
        builder.HasKey(a => a.Id);
        builder.HasOne<Ticket>().WithMany().HasForeignKey(a => new { a.TenantId, a.TicketId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<EscalationRule>().WithMany().HasForeignKey(a => new { a.TenantId, a.RuleId }).HasPrincipalKey(r => new { r.TenantId, r.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => new { a.TenantId, a.TicketId, a.RuleId }).IsUnique();
    }
}
