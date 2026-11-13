namespace Solvix.Modules.RemoteManagement.Persistence;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.RemoteManagement.Domain;

// Executions, approvals and audit rows deliberately have no foreign key to devices or users:
// history and the audit trail must outlive a deleted device or a removed member.
internal sealed class RemoteAccessPolicyConfiguration : IEntityTypeConfiguration<RemoteAccessPolicy>
{
    public void Configure(EntityTypeBuilder<RemoteAccessPolicy> builder)
    {
        builder.ToTable("remote_access_policies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.ApprovalRequiredCommands).HasColumnType("text[]").IsRequired();
        builder.Property(p => p.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.HasIndex(p => p.TenantId).IsUnique();
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_remote_access_policies_ttl", "command_ttl_minutes BETWEEN 5 AND 10080 AND approval_ttl_minutes BETWEEN 5 AND 10080");
            t.HasCheckConstraint("ck_remote_access_policies_window", "(allowed_from_minute IS NULL) = (allowed_to_minute IS NULL) AND COALESCE(allowed_from_minute, 0) BETWEEN 0 AND 1439 AND COALESCE(allowed_to_minute, 0) BETWEEN 0 AND 1440");
        });
    }
}

internal sealed class RemoteAccessScopeConfiguration : IEntityTypeConfiguration<RemoteAccessScope>
{
    public void Configure(EntityTypeBuilder<RemoteAccessScope> builder)
    {
        builder.ToTable("remote_access_scopes");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.DeviceGroupIds).HasColumnType("uuid[]");
        builder.Property(s => s.BranchIds).HasColumnType("uuid[]");
        builder.HasIndex(s => new { s.TenantId, s.UserId }).IsUnique();
    }
}

internal sealed class CommandExecutionConfiguration : IEntityTypeConfiguration<CommandExecution>
{
    public void Configure(EntityTypeBuilder<CommandExecution> builder)
    {
        builder.ToTable("remote_command_executions");
        builder.HasKey(e => e.Id);
        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
        builder.Property(e => e.DeviceName).HasMaxLength(255).IsRequired();
        builder.Property(e => e.CommandType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.RequiredPermission).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Parameters)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new Dictionary<string, string>(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                    v => new Dictionary<string, string>(v)))
            .IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(500);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(e => e.SourceIp).HasMaxLength(64);
        builder.Property(e => e.KeyId).HasMaxLength(64);
        builder.Property(e => e.SignedCommand).HasColumnType("text");
        builder.Property(e => e.Output).HasColumnType("text");
        builder.Property(e => e.ErrorCode).HasMaxLength(64);
        builder.Property(e => e.ErrorMessage).HasMaxLength(2000);

        builder.HasIndex(e => new { e.TenantId, e.DeviceId, e.Status });
        builder.HasIndex(e => new { e.TenantId, e.RequestedAtUtc });
        builder.HasIndex(e => new { e.Status, e.ExpiresAtUtc });
        builder.HasIndex(e => new { e.Status, e.ExecutionDeadlineUtc });
        builder.HasIndex(e => new { e.TenantId, e.DeviceId, e.Sequence }).IsUnique().HasFilter("sequence IS NOT NULL");
    }
}

internal sealed class RemoteApprovalConfiguration : IEntityTypeConfiguration<RemoteApproval>
{
    public void Configure(EntityTypeBuilder<RemoteApproval> builder)
    {
        builder.ToTable("remote_approvals");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.CommandType).HasMaxLength(64).IsRequired();
        builder.Property(a => a.DeviceName).HasMaxLength(255).IsRequired();
        builder.Property(a => a.Reason).HasMaxLength(500);
        builder.Property(a => a.DecisionComment).HasMaxLength(500);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.HasOne<CommandExecution>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.ExecutionId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.TenantId, a.ExecutionId }).IsUnique();
        builder.HasIndex(a => new { a.TenantId, a.Status, a.ExpiresAtUtc });

        // Four-eyes: whoever asked can never be the one who decided.
        builder.ToTable(t => t.HasCheckConstraint("ck_remote_approvals_four_eyes", "decided_by IS NULL OR decided_by <> requested_by"));
    }
}

internal sealed class DeviceCommandSequenceConfiguration : IEntityTypeConfiguration<DeviceCommandSequence>
{
    public void Configure(EntityTypeBuilder<DeviceCommandSequence> builder)
    {
        builder.ToTable("remote_device_command_sequences");
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => new { s.TenantId, s.DeviceId }).IsUnique();
    }
}

internal sealed class RemoteAuditEventConfiguration : IEntityTypeConfiguration<RemoteAuditEvent>
{
    public void Configure(EntityTypeBuilder<RemoteAuditEvent> builder)
    {
        builder.ToTable("remote_audit_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EventType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.CommandType).HasMaxLength(64);
        builder.Property(e => e.Permission).HasMaxLength(64);
        builder.Property(e => e.SourceIp).HasMaxLength(64);
        builder.Property(e => e.Detail).HasMaxLength(4000);
        builder.Property(e => e.PreviousHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(e => e.Hash).HasMaxLength(64).IsFixedLength().IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.Sequence }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.DeviceId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.ExecutionId });
    }
}
