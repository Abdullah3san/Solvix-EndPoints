namespace Solvix.Modules.Security.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Security.Domain;
using Solvix.Modules.Security.Evaluation;

internal static class SecurityConfigurationExtensions
{
    /// <summary>
    /// Tenant-safe foreign key to the endpoint: (tenant_id, endpoint_id) → endpoints(tenant_id, id),
    /// so the database itself refuses a child row whose endpoint belongs to another tenant.
    /// </summary>
    public static void HasEndpoint<T>(this EntityTypeBuilder<T> builder)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasOne<ManagedEndpoint>()
            .WithMany()
            .HasForeignKey("TenantId", "EndpointId")
            .HasPrincipalKey(nameof(ManagedEndpoint.TenantId), nameof(ManagedEndpoint.Id))
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>Optimistic concurrency via PostgreSQL's xmin system column (two approvers cannot both win).</summary>
    public static void UseXminConcurrency<T>(this EntityTypeBuilder<T> builder)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}

internal sealed class ManagedEndpointConfiguration : IEntityTypeConfiguration<ManagedEndpoint>
{
    public void Configure(EntityTypeBuilder<ManagedEndpoint> builder)
    {
        builder.ToTable("security_endpoints");
        builder.HasKey(e => e.Id);
        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
        builder.Property(e => e.Hostname).HasMaxLength(ManagedEndpoint.HostnameMaxLength).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.KeyHash).HasColumnType("bytea").IsRequired();
        builder.Property(e => e.OsCaption).HasMaxLength(ManagedEndpoint.TextMaxLength);
        builder.Property(e => e.OsVersion).HasMaxLength(ManagedEndpoint.TextMaxLength);
        builder.Property(e => e.AgentVersion).HasMaxLength(64);
        builder.Property(e => e.Band).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.Hostname });
        builder.HasIndex(e => new { e.TenantId, e.Band });
    }
}

internal sealed class PostureSnapshotConfiguration : IEntityTypeConfiguration<PostureSnapshot>
{
    public void Configure(EntityTypeBuilder<PostureSnapshot> builder)
    {
        builder.ToTable("security_snapshots");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Report).HasJsonbConversion();
        builder.Property(s => s.Evaluation).HasJsonbConversion();
        builder.Property(s => s.Band).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasEndpoint<PostureSnapshot>();
        builder.HasIndex(s => new { s.TenantId, s.EndpointId, s.ReceivedAtUtc }).IsDescending(false, false, true);
        builder.HasIndex(s => new { s.TenantId, s.ReceivedAtUtc });
    }
}

internal sealed class SecurityFindingConfiguration : IEntityTypeConfiguration<SecurityFinding>
{
    public void Configure(EntityTypeBuilder<SecurityFinding> builder)
    {
        builder.ToTable("security_findings");
        builder.HasKey(f => f.Id);
        builder.HasAlternateKey(f => new { f.TenantId, f.Id });
        builder.Property(f => f.RuleKey).HasMaxLength(SecurityFinding.RuleKeyMaxLength).IsRequired();
        builder.Property(f => f.SubjectKey).HasMaxLength(SecurityFinding.SubjectMaxLength).IsRequired();
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(f => f.Severity).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(f => f.Title).HasMaxLength(SecurityFinding.TitleMaxLength).IsRequired();
        builder.Property(f => f.Evidence).HasMaxLength(SecurityFinding.EvidenceMaxLength).IsRequired();
        builder.Property(f => f.RemediationActionKey).HasMaxLength(64);
        builder.Property(f => f.RemediationParameter).HasMaxLength(32);
        builder.Property(f => f.AcceptanceReason).HasMaxLength(500);
        builder.HasEndpoint<SecurityFinding>();
        builder.HasIndex(f => new { f.TenantId, f.EndpointId, f.RuleKey, f.SubjectKey }).IsUnique();
        builder.HasIndex(f => new { f.TenantId, f.Status, f.Severity });
        builder.UseXminConcurrency();
    }
}

internal sealed class SecurityAlertConfiguration : IEntityTypeConfiguration<SecurityAlert>
{
    public void Configure(EntityTypeBuilder<SecurityAlert> builder)
    {
        builder.ToTable("security_alerts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.RuleKey).HasMaxLength(SecurityFinding.RuleKeyMaxLength).IsRequired();
        builder.Property(a => a.DedupeKey).HasMaxLength(SecurityAlert.DedupeKeyMaxLength).IsRequired();
        builder.Property(a => a.Severity).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Title).HasMaxLength(300).IsRequired();
        builder.Property(a => a.Evidence).HasMaxLength(500).IsRequired();
        builder.Property(a => a.ClosureNote).HasMaxLength(500);
        builder.HasEndpoint<SecurityAlert>();
        builder.HasOne<SecurityFinding>()
            .WithMany()
            .HasForeignKey("TenantId", nameof(SecurityAlert.FindingId))
            .HasPrincipalKey(nameof(SecurityFinding.TenantId), nameof(SecurityFinding.Id))
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(a => new { a.TenantId, a.DedupeKey }).IsUnique();
        builder.HasIndex(a => new { a.TenantId, a.Status, a.RaisedAtUtc });
        builder.UseXminConcurrency();
    }
}

internal sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("security_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Log).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Provider).HasMaxLength(200);
        builder.Property(e => e.Summary).HasMaxLength(SecurityEvent.SummaryMaxLength);
        builder.Property(e => e.TargetGroup).HasMaxLength(100);
        builder.HasEndpoint<SecurityEvent>();
        builder.HasIndex(e => new { e.TenantId, e.EndpointId, e.Log, e.RecordId }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.TimeCreatedUtc });
    }
}

internal sealed class SecurityPolicyConfiguration : IEntityTypeConfiguration<SecurityPolicy>
{
    public void Configure(EntityTypeBuilder<SecurityPolicy> builder)
    {
        builder.ToTable("security_policies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).HasMaxLength(SecurityPolicy.NameMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(SecurityPolicy.DescriptionMaxLength);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Settings).HasJsonbConversion();
        builder.Property(p => p.DecisionNote).HasMaxLength(SecurityPolicy.NoteMaxLength);
        builder.HasIndex(p => new { p.TenantId, p.Version }).IsUnique();

        // At most one active policy per tenant, enforced by the database.
        builder.HasIndex(p => p.TenantId)
            .HasDatabaseName("ix_security_policies_tenant_active")
            .IsUnique()
            .HasFilter("status = 'Active'");
        builder.UseXminConcurrency();
    }
}

internal sealed class SoftwareAdvisoryConfiguration : IEntityTypeConfiguration<SoftwareAdvisory>
{
    public void Configure(EntityTypeBuilder<SoftwareAdvisory> builder)
    {
        builder.ToTable("security_advisories");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Title).HasMaxLength(SoftwareAdvisory.TitleMaxLength).IsRequired();
        builder.Property(a => a.ProductPattern).HasMaxLength(SoftwareAdvisory.PatternMaxLength).IsRequired();
        builder.Property(a => a.Publisher).HasMaxLength(200);
        builder.Property(a => a.AffectedFromVersion).HasMaxLength(SoftwareAdvisory.VersionMaxLength);
        builder.Property(a => a.FixedVersion).HasMaxLength(SoftwareAdvisory.VersionMaxLength).IsRequired();
        builder.Property(a => a.Severity).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Reference).HasMaxLength(SoftwareAdvisory.ReferenceMaxLength);
        builder.HasIndex(a => new { a.TenantId, a.IsEnabled });
    }
}

internal sealed class RemediationRequestConfiguration : IEntityTypeConfiguration<RemediationRequest>
{
    public void Configure(EntityTypeBuilder<RemediationRequest> builder)
    {
        builder.ToTable("security_remediations");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ActionKey).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Parameter).HasMaxLength(32);
        builder.Property(r => r.Risk).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(500).IsRequired();
        builder.Property(r => r.DecisionNote).HasMaxLength(500);
        builder.Property(r => r.ResultMessage).HasMaxLength(500);
        builder.HasEndpoint<RemediationRequest>();
        builder.HasOne<SecurityFinding>()
            .WithMany()
            .HasForeignKey("TenantId", nameof(RemediationRequest.FindingId))
            .HasPrincipalKey(nameof(SecurityFinding.TenantId), nameof(SecurityFinding.Id))
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(r => new { r.TenantId, r.Status, r.RequestedAtUtc });
        builder.HasIndex(r => new { r.TenantId, r.EndpointId, r.Status });
        builder.UseXminConcurrency();
    }
}

internal sealed class SecurityAuditEntryConfiguration : IEntityTypeConfiguration<SecurityAuditEntry>
{
    public void Configure(EntityTypeBuilder<SecurityAuditEntry> builder)
    {
        builder.ToTable("security_audit");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.ActorType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.ActorLabel).HasMaxLength(320).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(SecurityAuditEntry.ActionMaxLength).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(60).IsRequired();
        builder.Property(a => a.Summary).HasMaxLength(SecurityAuditEntry.SummaryMaxLength).IsRequired();
        builder.Property(a => a.Details).HasColumnType("text");
        builder.Property(a => a.SourceIp).HasMaxLength(45);
        builder.Property(a => a.CorrelationId).HasMaxLength(128);
        builder.HasIndex(a => new { a.TenantId, a.OccurredAtUtc }).IsDescending(false, true);
        builder.HasIndex(a => new { a.TenantId, a.Action });
        builder.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId });
    }
}
