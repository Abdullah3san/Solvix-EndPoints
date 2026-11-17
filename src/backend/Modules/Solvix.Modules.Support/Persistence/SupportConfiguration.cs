namespace Solvix.Modules.Support.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Support.Domain;

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Number).UseIdentityAlwaysColumn();
        builder.Property(t => t.Subject).HasMaxLength(Ticket.SubjectMaxLength).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(Ticket.DescriptionMaxLength);
        builder.Property(t => t.RequesterName).HasMaxLength(Ticket.RequesterMaxLength);
        builder.Property(t => t.RequesterEmail).HasMaxLength(Ticket.RequesterMaxLength);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Source).HasConversion<string>().HasMaxLength(20);

        builder.HasAlternateKey(t => new { t.TenantId, t.Id });
        builder.HasIndex(t => t.Number).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.Status, t.CreatedAtUtc });
        builder.HasIndex(t => new { t.TenantId, t.DeviceId });

        // DeviceId / AssignedUserId reference other modules; their tenant-safe foreign keys
        // (ON DELETE SET NULL) are declared in the SupportTickets migration.
    }
}

internal sealed class TicketCommentConfiguration : IEntityTypeConfiguration<TicketComment>
{
    public void Configure(EntityTypeBuilder<TicketComment> builder)
    {
        builder.ToTable("ticket_comments");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Body).HasMaxLength(Ticket.DescriptionMaxLength).IsRequired();
        builder.Property(c => c.AuthorName).HasMaxLength(TicketComment.AuthorNameMaxLength);
        builder.Property(c => c.Author).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.TenantId, c.TicketId, c.CreatedAtUtc });
    }
}

internal sealed class DeviceChatMessageConfiguration : IEntityTypeConfiguration<DeviceChatMessage>
{
    public void Configure(EntityTypeBuilder<DeviceChatMessage> builder)
    {
        builder.ToTable("device_chat_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Sequence).UseIdentityAlwaysColumn();
        builder.Property(m => m.Body).HasMaxLength(Solvix.Agent.Contracts.ChatMessage.BodyMaxLength).IsRequired();
        builder.Property(m => m.SenderName).HasMaxLength(200);
        builder.Property(m => m.Sender).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(m => new { m.TenantId, m.DeviceId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => m.Sequence).IsUnique();
        builder.HasIndex(m => new { m.TenantId, m.DeviceId, m.Sequence });
    }
}
