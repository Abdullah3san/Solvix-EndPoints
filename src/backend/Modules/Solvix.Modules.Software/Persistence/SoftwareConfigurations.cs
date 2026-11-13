namespace Solvix.Modules.Software.Persistence;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Software.Domain;

internal sealed class SoftwarePackageConfiguration : IEntityTypeConfiguration<SoftwarePackage>
{
    public void Configure(EntityTypeBuilder<SoftwarePackage> builder)
    {
        builder.ToTable("software_packages");
        builder.HasKey(p => p.Id);
        builder.HasAlternateKey(p => new { p.TenantId, p.Id });
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Publisher).HasMaxLength(200);
        builder.Property(p => p.Version).HasMaxLength(64);
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.InstallerType).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(p => p.OriginalFileName).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(p => p.StorageKey).HasMaxLength(32).IsFixedLength().IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.SignatureState).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.SignerSubject).HasMaxLength(1000);
        builder.Property(p => p.SignerThumbprint).HasMaxLength(64).IsFixedLength();
        builder.Property(p => p.Architecture).HasMaxLength(16);
        builder.Property(p => p.SilentPreset).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.InstallArguments).HasColumnType("text[]").IsRequired();
        builder.Property(p => p.UninstallArguments).HasColumnType("text[]").IsRequired();
        builder.Property(p => p.SuccessExitCodes).HasColumnType("integer[]").IsRequired();
        builder.Property(p => p.RebootExitCodes).HasColumnType("integer[]").IsRequired();
        builder.Property(p => p.DetectionKind).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.DetectionProductCode).HasMaxLength(38);
        builder.Property(p => p.DetectionDisplayName).HasMaxLength(256);
        builder.Property(p => p.DetectionNameMatch).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.DetectionPublisher).HasMaxLength(256);
        builder.Property(p => p.DetectionVersion).HasMaxLength(64);
        builder.Property(p => p.DetectionVersionOperator).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Ignore(p => p.Detection);
        builder.Ignore(p => p.IsConfigured);
        builder.Ignore(p => p.CanUninstall);

        // One live copy of a file per tenant (duplicate uploads are refused, also under races).
        builder.HasIndex(p => new { p.TenantId, p.Sha256 }).IsUnique().HasFilter("status <> 'Retired'");
        builder.HasIndex(p => new { p.TenantId, p.Name });
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_software_packages_size", "size_bytes > 0");
            t.HasCheckConstraint("ck_software_packages_timeout", "timeout_minutes BETWEEN 1 AND 240");
        });
    }
}

internal sealed class SoftwareDeploymentConfiguration : IEntityTypeConfiguration<SoftwareDeployment>
{
    public void Configure(EntityTypeBuilder<SoftwareDeployment> builder)
    {
        builder.ToTable("software_deployments");
        builder.HasKey(d => d.Id);
        builder.HasAlternateKey(d => new { d.TenantId, d.Id });
        builder.Property(d => d.Name).HasMaxLength(200).IsRequired();
        builder.Property(d => d.Reason).HasMaxLength(500);
        builder.Property(d => d.Action).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.PackageName).HasMaxLength(200).IsRequired();
        builder.Property(d => d.PackageVersion).HasMaxLength(64);
        builder.Property(d => d.PackageSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(d => d.InstallerType).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(d => d.Parameters)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new Dictionary<string, string>(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                    v => new Dictionary<string, string>(v)))
            .IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(d => d.SensitivityReasons).HasColumnType("text[]").IsRequired();
        builder.Property(d => d.ReportJson).HasColumnType("jsonb");

        // Deployments keep their package snapshot; the package row itself must not disappear under them.
        builder.HasOne<SoftwarePackage>()
            .WithMany()
            .HasForeignKey(d => new { d.TenantId, d.PackageId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => new { d.TenantId, d.Status });
        builder.HasIndex(d => new { d.TenantId, d.RequestedAtUtc });
        builder.HasIndex(d => new { d.Status, d.ScheduledStartUtc });
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_software_deployments_approvals", "required_approvals BETWEEN 0 AND 2");
            t.HasCheckConstraint("ck_software_deployments_attempts", "max_attempts BETWEEN 1 AND 5");
            t.HasCheckConstraint("ck_software_deployments_window", "deadline_utc > scheduled_start_utc");
        });
    }
}

internal sealed class DeploymentTargetConfiguration : IEntityTypeConfiguration<DeploymentTarget>
{
    public void Configure(EntityTypeBuilder<DeploymentTarget> builder)
    {
        builder.ToTable("software_deployment_targets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.DeviceName).HasMaxLength(255).IsRequired();
        builder.Property(t => t.Action).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.Outcome).HasConversion<string>().HasMaxLength(24);
        builder.Property(t => t.ErrorCode).HasMaxLength(64);
        builder.Property(t => t.ErrorMessage).HasMaxLength(2000);
        builder.Property(t => t.DetectedVersion).HasMaxLength(128);
        builder.Property(t => t.LogExcerpt).HasMaxLength(8192);

        builder.HasOne<SoftwareDeployment>()
            .WithMany()
            .HasForeignKey(t => new { t.TenantId, t.DeploymentId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => new { t.TenantId, t.DeploymentId, t.DeviceId }).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.DeviceId });
        builder.HasIndex(t => t.CurrentExecutionId).IsUnique().HasFilter("current_execution_id IS NOT NULL");
        builder.HasIndex(t => new { t.Status, t.NextAttemptAtUtc });

        // At most one unfinished deployment of a package per device: duplicate deployments are refused by the database too.
        builder.HasIndex(t => new { t.TenantId, t.DeviceId, t.PackageId })
            .IsUnique()
            .HasFilter("status IN ('Pending', 'Dispatched', 'Running')")
            .HasDatabaseName("ux_software_deployment_targets_active");
        builder.ToTable(t => t.HasCheckConstraint("ck_software_deployment_targets_attempts", "attempts BETWEEN 0 AND 5"));
    }
}

internal sealed class DeploymentApprovalConfiguration : IEntityTypeConfiguration<DeploymentApproval>
{
    public void Configure(EntityTypeBuilder<DeploymentApproval> builder)
    {
        builder.ToTable("software_deployment_approvals");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Decision).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Comment).HasMaxLength(500);
        builder.HasOne<SoftwareDeployment>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.DeploymentId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => new { a.TenantId, a.DeploymentId, a.ApproverId }).IsUnique();

        // Four-eyes: the requester can never decide on their own deployment.
        builder.ToTable(t => t.HasCheckConstraint("ck_software_deployment_approvals_four_eyes", "approver_id <> requested_by"));
    }
}

internal sealed class DeploymentGroupConfiguration : IEntityTypeConfiguration<DeploymentGroup>
{
    public void Configure(EntityTypeBuilder<DeploymentGroup> builder)
    {
        builder.ToTable("software_deployment_groups");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Name).HasMaxLength(120).IsRequired();
        builder.Property(g => g.Description).HasMaxLength(500);
        builder.Property(g => g.DeviceIds).HasColumnType("uuid[]").IsRequired();
        builder.Property(g => g.DeviceGroupIds).HasColumnType("uuid[]").IsRequired();
        builder.Property(g => g.BranchIds).HasColumnType("uuid[]").IsRequired();
        builder.Property(g => g.ExcludedDeviceIds).HasColumnType("uuid[]").IsRequired();
        builder.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
    }
}

internal sealed class SoftwareDeploymentPolicyConfiguration : IEntityTypeConfiguration<SoftwareDeploymentPolicy>
{
    public void Configure(EntityTypeBuilder<SoftwareDeploymentPolicy> builder)
    {
        builder.ToTable("software_deployment_policies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.TrustedPublisherThumbprints).HasColumnType("text[]").IsRequired();
        builder.HasIndex(p => p.TenantId).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("ck_software_deployment_policies_values", "large_deployment_threshold BETWEEN 1 AND 5000 AND approval_ttl_hours BETWEEN 1 AND 720"));
    }
}

internal sealed class SoftwareRestrictionRuleConfiguration : IEntityTypeConfiguration<SoftwareRestrictionRule>
{
    public void Configure(EntityTypeBuilder<SoftwareRestrictionRule> builder)
    {
        builder.ToTable("software_restriction_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).HasMaxLength(120).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(500);
        builder.Property(r => r.RuleType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.NamePattern).HasMaxLength(256).IsRequired();
        builder.Property(r => r.Publisher).HasMaxLength(256);
        builder.Property(r => r.MinimumVersion).HasMaxLength(64);
        builder.Property(r => r.DeviceGroupIds).HasColumnType("uuid[]");
        builder.HasIndex(r => new { r.TenantId, r.Enabled });
    }
}

internal sealed class SoftwareInventoryItemConfiguration : IEntityTypeConfiguration<SoftwareInventoryItem>
{
    public void Configure(EntityTypeBuilder<SoftwareInventoryItem> builder)
    {
        builder.ToTable("software_inventory_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Name).HasMaxLength(256).IsRequired();
        builder.Property(i => i.NameKey).HasMaxLength(256).IsRequired();
        builder.Property(i => i.Version).HasMaxLength(128);
        builder.Property(i => i.Publisher).HasMaxLength(256);
        builder.Property(i => i.Scope).HasMaxLength(16);
        builder.HasIndex(i => new { i.TenantId, i.DeviceId });
        builder.HasIndex(i => new { i.TenantId, i.NameKey });
    }
}

internal sealed class SoftwareAuditEventConfiguration : IEntityTypeConfiguration<SoftwareAuditEvent>
{
    public void Configure(EntityTypeBuilder<SoftwareAuditEvent> builder)
    {
        builder.ToTable("software_audit_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EventType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.SourceIp).HasMaxLength(64);
        builder.Property(e => e.Detail).HasMaxLength(4000);
        builder.Property(e => e.PreviousHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(e => e.Hash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.Sequence }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.DeploymentId });
        builder.HasIndex(e => new { e.TenantId, e.PackageId });
        builder.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });
    }
}
