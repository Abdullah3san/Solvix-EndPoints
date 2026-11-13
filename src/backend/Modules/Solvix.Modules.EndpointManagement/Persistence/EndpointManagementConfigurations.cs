namespace Solvix.Modules.EndpointManagement.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;
using Solvix.Modules.EndpointManagement.Domain.Patches;

/// <summary>
/// Every reference between tenant-owned rows uses a composite (tenant_id, id)
/// foreign key, so the database itself guarantees that a deployment, assignment
/// or state row can never point at another tenant's device, patch or policy.
/// </summary>
internal static class TenantSafe
{
    public static void ReferencesDevice<T>(this EntityTypeBuilder<T> builder, System.Linq.Expressions.Expression<Func<T, object?>> foreignKey, DeleteBehavior onDelete = DeleteBehavior.Cascade)
        where T : class =>
        builder.HasOne<Device>().WithMany().HasForeignKey(foreignKey).HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(onDelete);
}

internal sealed class MaintenanceWindowConfiguration : IEntityTypeConfiguration<MaintenanceWindow>
{
    public void Configure(EntityTypeBuilder<MaintenanceWindow> builder)
    {
        builder.ToTable("maintenance_windows");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Name).HasMaxLength(MaintenanceWindow.NameMaxLength).IsRequired();
        builder.Property(w => w.Description).HasMaxLength(MaintenanceWindow.DescriptionMaxLength);
        builder.Property(w => w.TimeZoneId).HasMaxLength(MaintenanceWindow.TimeZoneMaxLength).IsRequired();
        builder.Property(w => w.Recurrence).HasConversion<string>().HasMaxLength(16);
        builder.Property(w => w.DaysOfWeek).HasConversion<int>();
        builder.HasAlternateKey(w => new { w.TenantId, w.Id });
        builder.HasIndex(w => new { w.TenantId, w.Name }).IsUnique();
    }
}

internal sealed class PatchConfiguration : IEntityTypeConfiguration<Patch>
{
    public void Configure(EntityTypeBuilder<Patch> builder)
    {
        builder.ToTable("patches");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.UpdateId).HasMaxLength(Patch.UpdateIdMaxLength).IsRequired();
        builder.Property(p => p.Title).HasMaxLength(Patch.TitleMaxLength).IsRequired();
        builder.Property(p => p.SupportUrl).HasMaxLength(Patch.UrlMaxLength);
        builder.Property(p => p.ApprovalNote).HasMaxLength(Patch.NoteMaxLength);
        builder.Property(p => p.Classification).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.Severity).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.RebootBehavior).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.ApprovalState).HasConversion<string>().HasMaxLength(16);
        builder.Ignore(p => p.IsSecurityRelevant);
        builder.HasAlternateKey(p => new { p.TenantId, p.Id });
        builder.HasIndex(p => new { p.TenantId, p.UpdateId, p.RevisionNumber }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.ApprovalState });
    }
}

internal sealed class DevicePatchStateConfiguration : IEntityTypeConfiguration<DevicePatchState>
{
    public void Configure(EntityTypeBuilder<DevicePatchState> builder)
    {
        builder.ToTable("device_patch_states");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        builder.Ignore(s => s.IsOutstanding);
        builder.ReferencesDevice(s => new { s.TenantId, s.DeviceId });
        builder.HasOne(s => s.Patch).WithMany().HasForeignKey(s => new { s.TenantId, s.PatchId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => new { s.TenantId, s.DeviceId, s.PatchId }).IsUnique();
        builder.HasIndex(s => new { s.TenantId, s.PatchId, s.Status });
    }
}

internal sealed class DeviceUpdateSummaryConfiguration : IEntityTypeConfiguration<DeviceUpdateSummary>
{
    public void Configure(EntityTypeBuilder<DeviceUpdateSummary> builder)
    {
        builder.ToTable("device_update_summaries");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.LastScanResult).HasConversion<string>().HasMaxLength(32);
        builder.Property(s => s.LastScanError).HasMaxLength(1000);
        builder.Property(s => s.ServiceState).HasMaxLength(64);
        builder.Property(s => s.AutomaticUpdatesNotificationLevel).HasMaxLength(64);
        builder.Property(s => s.UpdateSource).HasMaxLength(64);
        builder.ReferencesDevice(s => new { s.TenantId, s.DeviceId });
        builder.HasIndex(s => new { s.TenantId, s.DeviceId }).IsUnique();
    }
}

internal sealed class PatchAutoApprovalRuleConfiguration : IEntityTypeConfiguration<PatchAutoApprovalRule>
{
    public void Configure(EntityTypeBuilder<PatchAutoApprovalRule> builder)
    {
        builder.ToTable("patch_auto_approval_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).HasMaxLength(PatchAutoApprovalRule.NameMaxLength).IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
    }
}

internal sealed class PatchDeploymentConfiguration : IEntityTypeConfiguration<PatchDeployment>
{
    public void Configure(EntityTypeBuilder<PatchDeployment> builder)
    {
        builder.ToTable("patch_deployments");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Name).HasMaxLength(PatchDeployment.NameMaxLength).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(PatchDeployment.DescriptionMaxLength);
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(d => d.Phase).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.RestartBehavior).HasConversion<string>().HasMaxLength(32);
        builder.Property(d => d.HaltReason).HasMaxLength(PatchDeployment.ReasonMaxLength);
        builder.Property(d => d.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.Ignore(d => d.HasPilot);
        builder.Ignore(d => d.IsTerminal);
        builder.Ignore(d => d.IsDispatching);
        builder.HasAlternateKey(d => new { d.TenantId, d.Id });

        builder.HasOne<MaintenanceWindow>().WithMany().HasForeignKey(d => new { d.TenantId, d.MaintenanceWindowId })
            .HasPrincipalKey(w => new { w.TenantId, w.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DeviceGroup>().WithMany().HasForeignKey(d => new { d.TenantId, d.PilotDeviceGroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id }).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(d => d.Patches).WithOne().HasForeignKey(p => new { p.TenantId, p.DeploymentId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(d => d.Targets).WithOne().HasForeignKey(t => new { t.TenantId, t.DeploymentId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(d => d.Patches).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(d => d.Targets).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(d => new { d.TenantId, d.Status });
    }
}

internal sealed class PatchDeploymentPatchConfiguration : IEntityTypeConfiguration<PatchDeploymentPatch>
{
    public void Configure(EntityTypeBuilder<PatchDeploymentPatch> builder)
    {
        builder.ToTable("patch_deployment_patches");
        builder.HasKey(p => p.Id);
        builder.HasOne<Patch>().WithMany().HasForeignKey(p => new { p.TenantId, p.PatchId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.TenantId, p.DeploymentId, p.PatchId }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.PatchId });
    }
}

internal sealed class PatchDeploymentTargetConfiguration : IEntityTypeConfiguration<PatchDeploymentTarget>
{
    public void Configure(EntityTypeBuilder<PatchDeploymentTarget> builder)
    {
        builder.ToTable("patch_deployment_targets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.TargetType).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(t => new { t.TenantId, t.DeploymentId });
        builder.HasIndex(t => new { t.TenantId, t.TargetType, t.TargetId });
    }
}

internal sealed class PatchDeploymentJobConfiguration : IEntityTypeConfiguration<PatchDeploymentJob>
{
    public void Configure(EntityTypeBuilder<PatchDeploymentJob> builder)
    {
        builder.ToTable("patch_deployment_jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Ring).HasConversion<string>().HasMaxLength(16);
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(j => j.Stage).HasConversion<string>().HasMaxLength(16);
        builder.Property(j => j.Message).HasMaxLength(PatchDeploymentJob.MessageMaxLength);
        builder.Property(j => j.LastError).HasMaxLength(PatchDeploymentJob.MessageMaxLength);
        builder.Property(j => j.ResultsJson).HasColumnType("jsonb");
        builder.Property(j => j.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.Ignore(j => j.IsTerminal);
        builder.Ignore(j => j.IsSettled);
        builder.Ignore(j => j.IsInFlight);
        builder.Ignore(j => j.CountsAsSuccess);
        builder.Ignore(j => j.CountsAsFailure);

        builder.HasOne<PatchDeployment>().WithMany().HasForeignKey(j => new { j.TenantId, j.DeploymentId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.ReferencesDevice(j => new { j.TenantId, j.DeviceId });

        builder.HasIndex(j => new { j.TenantId, j.DeploymentId, j.DeviceId }).IsUnique();
        builder.HasIndex(j => new { j.TenantId, j.DeviceId, j.Status });
        builder.HasIndex(j => new { j.TenantId, j.Status, j.LeaseExpiresUtc });
    }
}

internal sealed class DeviceRestartRequestConfiguration : IEntityTypeConfiguration<DeviceRestartRequest>
{
    public void Configure(EntityTypeBuilder<DeviceRestartRequest> builder)
    {
        builder.ToTable("device_restart_requests");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Source).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Reason).HasMaxLength(DeviceRestartRequest.ReasonMaxLength).IsRequired();
        builder.Property(r => r.Message).HasMaxLength(500);
        builder.Ignore(r => r.IsOpen);
        builder.ReferencesDevice(r => new { r.TenantId, r.DeviceId });
        builder.HasOne<MaintenanceWindow>().WithMany().HasForeignKey(r => new { r.TenantId, r.MaintenanceWindowId })
            .HasPrincipalKey(w => new { w.TenantId, w.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.DeviceId, r.Status });
    }
}

internal sealed class ConfigurationPolicyConfiguration : IEntityTypeConfiguration<ConfigurationPolicy>
{
    public void Configure(EntityTypeBuilder<ConfigurationPolicy> builder)
    {
        builder.ToTable("configuration_policies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).HasMaxLength(ConfigurationPolicy.NameMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(ConfigurationPolicy.DescriptionMaxLength);
        builder.Property(p => p.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
        builder.HasAlternateKey(p => new { p.TenantId, p.Id });
        builder.HasIndex(p => new { p.TenantId, p.Name }).IsUnique();
    }
}

internal sealed class ConfigurationPolicyVersionConfiguration : IEntityTypeConfiguration<ConfigurationPolicyVersion>
{
    public void Configure(EntityTypeBuilder<ConfigurationPolicyVersion> builder)
    {
        builder.ToTable("configuration_policy_versions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(v => v.SettingsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(v => v.MaxRisk).HasConversion<string>().HasMaxLength(16);
        builder.Property(v => v.AuthorizedRisk).HasConversion<string>().HasMaxLength(16);
        builder.Property(v => v.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(v => v.ChangeNote).HasMaxLength(ConfigurationPolicyVersion.NoteMaxLength);
        builder.Property(v => v.ReviewNote).HasMaxLength(ConfigurationPolicyVersion.NoteMaxLength);
        builder.HasAlternateKey(v => new { v.TenantId, v.Id });
        builder.HasOne(v => v.Policy).WithMany().HasForeignKey(v => new { v.TenantId, v.PolicyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(v => new { v.TenantId, v.PolicyId, v.Number }).IsUnique();

        // At most one active version per policy, enforced by the database.
        builder.HasIndex(v => new { v.TenantId, v.PolicyId })
            .HasDatabaseName("ix_configuration_policy_versions_single_published")
            .IsUnique()
            .HasFilter("status = 'Published'");
    }
}

internal sealed class ConfigurationAssignmentConfiguration : IEntityTypeConfiguration<ConfigurationAssignment>
{
    public void Configure(EntityTypeBuilder<ConfigurationAssignment> builder)
    {
        builder.ToTable("configuration_assignments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.TargetType).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.Mode).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.RemovalBehavior).HasConversion<string>().HasMaxLength(16);
        builder.HasOne<ConfigurationPolicy>().WithMany().HasForeignKey(a => new { a.TenantId, a.PolicyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MaintenanceWindow>().WithMany().HasForeignKey(a => new { a.TenantId, a.MaintenanceWindowId })
            .HasPrincipalKey(w => new { w.TenantId, w.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.TenantId, a.PolicyId, a.TargetType, a.TargetId, a.IsExclusion }).IsUnique().AreNullsDistinct(false);
        builder.HasIndex(a => new { a.TenantId, a.TargetType, a.TargetId });
    }
}

internal sealed class DeviceConfigurationStateConfiguration : IEntityTypeConfiguration<DeviceConfigurationState>
{
    public void Configure(EntityTypeBuilder<DeviceConfigurationState> builder)
    {
        builder.ToTable("device_configuration_states");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.SettingKey).HasMaxLength(DeviceConfigurationState.KeyMaxLength).IsRequired();
        builder.Property(s => s.Mode).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.RemovalBehavior).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(s => s.ExpectedHash).HasMaxLength(64);
        builder.Property(s => s.ExpectedValue).HasMaxLength(DeviceConfigurationState.ValueMaxLength);
        builder.Property(s => s.ActualValue).HasMaxLength(DeviceConfigurationState.ValueMaxLength);
        builder.Property(s => s.Message).HasMaxLength(DeviceConfigurationState.ValueMaxLength);
        builder.Ignore(s => s.NeedsRevert);
        builder.ReferencesDevice(s => new { s.TenantId, s.DeviceId });
        builder.HasIndex(s => new { s.TenantId, s.DeviceId, s.SettingKey }).IsUnique();
        builder.HasIndex(s => new { s.TenantId, s.PolicyId, s.Status });
    }
}

internal sealed class ConfigurationDriftEventConfiguration : IEntityTypeConfiguration<ConfigurationDriftEvent>
{
    public void Configure(EntityTypeBuilder<ConfigurationDriftEvent> builder)
    {
        builder.ToTable("configuration_drift_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.SettingKey).HasMaxLength(DeviceConfigurationState.KeyMaxLength).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Mode).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.ExpectedValue).HasMaxLength(DeviceConfigurationState.ValueMaxLength);
        builder.Property(e => e.ActualValue).HasMaxLength(DeviceConfigurationState.ValueMaxLength);
        builder.ReferencesDevice(e => new { e.TenantId, e.DeviceId });
        builder.HasIndex(e => new { e.TenantId, e.DetectedAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.DeviceId, e.DetectedAtUtc });
    }
}
