namespace Solvix.Modules.Assistant.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Assistant.Domain;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<AssistantConversation>
{
    public void Configure(EntityTypeBuilder<AssistantConversation> builder)
    {
        builder.ToTable("assistant_conversations");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Title).HasMaxLength(AssistantConversation.TitleMaxLength).IsRequired();
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });
        builder.HasIndex(c => new { c.TenantId, c.UserId, c.LastMessageAtUtc });
    }
}

internal sealed class MessageConfiguration : IEntityTypeConfiguration<AssistantMessage>
{
    public void Configure(EntityTypeBuilder<AssistantMessage> builder)
    {
        builder.ToTable("assistant_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(m => m.Content).IsRequired();
        builder.Property(m => m.SourcesJson).HasColumnType("jsonb");
        builder.Property(m => m.ToolCallsJson).HasColumnType("jsonb");
        builder.Property(m => m.ArtifactsJson).HasColumnType("jsonb");
        builder.Property(m => m.Model).HasMaxLength(100);
        builder.HasOne<AssistantConversation>()
            .WithMany()
            .HasForeignKey(m => new { m.TenantId, m.ConversationId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(m => new { m.TenantId, m.ConversationId, m.Id });
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AssistantAuditEntry>
{
    public void Configure(EntityTypeBuilder<AssistantAuditEntry> builder)
    {
        builder.ToTable("assistant_audit_log");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Kind).HasMaxLength(AssistantAuditEntry.KindMaxLength).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(AssistantAuditEntry.NameMaxLength).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.UserEmail).HasMaxLength(320);
        builder.Property(a => a.InputJson).HasColumnType("text");
        builder.Property(a => a.Summary).HasColumnType("text");
        builder.Property(a => a.CorrelationId).HasMaxLength(100);
        builder.Property(a => a.Model).HasMaxLength(100);
        builder.HasIndex(a => new { a.TenantId, a.CreatedAtUtc });
        builder.HasIndex(a => new { a.TenantId, a.Kind });
        builder.HasIndex(a => new { a.TenantId, a.ProposalId });
    }
}

internal sealed class RemediationProposalConfiguration : IEntityTypeConfiguration<RemediationProposal>
{
    public void Configure(EntityTypeBuilder<RemediationProposal> builder)
    {
        builder.ToTable("assistant_proposals");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.ProposedByEmail).HasMaxLength(320);
        builder.Property(p => p.Action).HasMaxLength(RemediationProposal.ActionMaxLength).IsRequired();
        builder.Property(p => p.Title).HasMaxLength(RemediationProposal.TitleMaxLength).IsRequired();
        builder.Property(p => p.Rationale).HasMaxLength(2000).IsRequired();
        builder.Property(p => p.Risk).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(p => p.ParametersJson).HasColumnType("jsonb").IsRequired();
        builder.Property(p => p.TargetsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(p => p.ResultJson).HasColumnType("jsonb");
        builder.Property(p => p.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(p => p.DecidedByEmail).HasMaxLength(320);
        builder.Property(p => p.DecisionNote).HasMaxLength(500);

        // Optimistic concurrency on the PostgreSQL system column: two simultaneous approvals cannot both win.
        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasIndex(p => new { p.TenantId, p.Status, p.CreatedAtUtc });
        builder.HasIndex(p => new { p.TenantId, p.ProposedByUserId });
    }
}

internal sealed class TicketConfiguration : IEntityTypeConfiguration<AssistantTicket>
{
    public void Configure(EntityTypeBuilder<AssistantTicket> builder)
    {
        builder.ToTable("assistant_tickets");
        builder.HasKey(t => t.Id);
        builder.Ignore(t => t.Reference);
        builder.Property(t => t.Title).HasMaxLength(AssistantTicket.TitleMaxLength).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(AssistantTicket.DescriptionMaxLength).IsRequired();
        builder.Property(t => t.Category).HasMaxLength(AssistantTicket.CategoryMaxLength);
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.DeviceRefsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(t => t.CreatedByEmail).HasMaxLength(320);
        builder.HasIndex(t => new { t.TenantId, t.Number }).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.Status });
    }
}

internal sealed class TicketCounterConfiguration : IEntityTypeConfiguration<TicketCounter>
{
    public void Configure(EntityTypeBuilder<TicketCounter> builder)
    {
        builder.ToTable("assistant_ticket_counters");
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => c.TenantId).IsUnique();
    }
}

internal sealed class KnowledgeDocumentConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocument> builder)
    {
        builder.ToTable("knowledge_documents");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Title).HasMaxLength(KnowledgeDocument.TitleMaxLength).IsRequired();
        builder.Property(d => d.FileName).HasMaxLength(KnowledgeDocument.FileNameMaxLength).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(d => d.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(d => d.FailureReason).HasMaxLength(500);
        builder.Property(d => d.EmbeddingModel).HasMaxLength(100).IsRequired();
        builder.Property(d => d.ExtractedText).HasColumnType("text");
        builder.HasAlternateKey(d => new { d.TenantId, d.Id });
        builder.HasIndex(d => new { d.TenantId, d.ContentHash }).IsUnique();
    }
}

internal sealed class KnowledgeChunkConfiguration : IEntityTypeConfiguration<KnowledgeChunk>
{
    public void Configure(EntityTypeBuilder<KnowledgeChunk> builder)
    {
        builder.ToTable("knowledge_chunks");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Content).IsRequired();
        builder.Property(c => c.Section).HasMaxLength(300);
        builder.Property(c => c.Embedding).HasColumnType("real[]").IsRequired();
        builder.Property(c => c.EmbeddingModel).HasMaxLength(100).IsRequired();
        builder.HasOne<KnowledgeDocument>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.DocumentId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.TenantId, c.DocumentId, c.Ordinal }).IsUnique();
    }
}

internal sealed class ReportConfiguration : IEntityTypeConfiguration<AssistantReport>
{
    public void Configure(EntityTypeBuilder<AssistantReport> builder)
    {
        builder.ToTable("assistant_reports");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Title).HasMaxLength(AssistantReport.TitleMaxLength).IsRequired();
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(r => r.ContentMarkdown).IsRequired();
        builder.Property(r => r.ParametersJson).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.GeneratedByEmail).HasMaxLength(320);
        builder.Property(r => r.Model).HasMaxLength(100);
        builder.HasIndex(r => new { r.TenantId, r.CreatedAtUtc });
    }
}
