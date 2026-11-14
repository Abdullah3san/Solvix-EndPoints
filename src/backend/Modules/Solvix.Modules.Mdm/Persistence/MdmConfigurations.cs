namespace Solvix.Modules.Mdm.Persistence;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Mdm.Domain;

internal static class MdmJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

internal sealed class MobilePolicyConfiguration : IEntityTypeConfiguration<MobilePolicy>
{
    public void Configure(EntityTypeBuilder<MobilePolicy> builder)
    {
        builder.ToTable("mobile_policies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).HasMaxLength(MobilePolicy.NameMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(MobilePolicy.DescriptionMaxLength);
        builder.Property(p => p.Platform).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.Mode).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(p => p.Ownership).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(p => p.Version).IsRequired();
        builder.Property(p => p.PublishedVersion).IsRequired();
        builder.Property(p => p.Settings)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, MdmJson.Options),
                s => JsonSerializer.Deserialize<MobilePolicySettings>(s, MdmJson.Options) ?? new MobilePolicySettings())
            .IsRequired();
        builder.Ignore(p => p.Scenario);

        builder.HasAlternateKey(p => new { p.TenantId, p.Id });
        builder.HasIndex(p => new { p.TenantId, p.Name }).IsUnique();
    }
}

internal sealed class MobileAppConfiguration : IEntityTypeConfiguration<MobileApp>
{
    public void Configure(EntityTypeBuilder<MobileApp> builder)
    {
        builder.ToTable("mobile_apps");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Platform).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(MobileApp.NameMaxLength).IsRequired();
        builder.Property(a => a.Identifier).HasMaxLength(MobileApp.IdentifierMaxLength).IsRequired();
        builder.Property(a => a.Source).HasConversion<string>().HasMaxLength(24).IsRequired();

        builder.HasAlternateKey(a => new { a.TenantId, a.Id });
        builder.HasIndex(a => new { a.TenantId, a.Platform, a.Identifier }).IsUnique();
    }
}

internal sealed class MobilePolicyAppConfiguration : IEntityTypeConfiguration<MobilePolicyApp>
{
    public void Configure(EntityTypeBuilder<MobilePolicyApp> builder)
    {
        builder.ToTable("mobile_policy_apps");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.InstallType).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(a => a.ManagedConfigurationJson).HasColumnType("jsonb");

        builder.HasOne<MobilePolicy>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.PolicyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MobileApp>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.AppId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.TenantId, a.PolicyId, a.AppId }).IsUnique();
    }
}

internal sealed class EnrollmentTokenConfiguration : IEntityTypeConfiguration<EnrollmentToken>
{
    public void Configure(EntityTypeBuilder<EnrollmentToken> builder)
    {
        builder.ToTable("mobile_enrollment_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(EnrollmentToken.NameMaxLength).IsRequired();
        builder.Property(t => t.Platform).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(t => t.Mode).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(t => t.Ownership).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(t => t.Method).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(t => t.SecretHash).HasMaxLength(64);
        builder.Property(t => t.ProviderReference).HasMaxLength(512);
        builder.Ignore(t => t.Scenario);

        builder.HasAlternateKey(t => new { t.TenantId, t.Id });
        builder.HasOne<MobilePolicy>().WithMany()
            .HasForeignKey(t => new { t.TenantId, t.PolicyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => t.SecretHash).IsUnique().HasFilter("secret_hash IS NOT NULL");
        builder.HasIndex(t => t.ProviderReference).HasFilter("provider_reference IS NOT NULL");
    }
}

internal sealed class MobileDeviceConfiguration : IEntityTypeConfiguration<MobileDevice>
{
    public void Configure(EntityTypeBuilder<MobileDevice> builder)
    {
        builder.ToTable("mobile_devices");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Platform).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.Mode).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(d => d.Ownership).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(d => d.State).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(d => d.Integrity).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.Compliance).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(d => d.ProviderDeviceId).HasMaxLength(MobileDevice.IdentifierMaxLength).IsRequired();
        builder.Property(d => d.ManagementIdentifier).HasMaxLength(MobileDevice.IdentifierMaxLength);
        builder.Property(d => d.DisplayName).HasMaxLength(MobileDevice.TextMaxLength);
        builder.Property(d => d.Manufacturer).HasMaxLength(MobileDevice.TextMaxLength);
        builder.Property(d => d.Model).HasMaxLength(MobileDevice.TextMaxLength);
        builder.Property(d => d.SerialNumber).HasMaxLength(MobileDevice.TextMaxLength);
        builder.Property(d => d.OsVersion).HasMaxLength(64);
        builder.Property(d => d.SecurityPatchLevel).HasMaxLength(64);
        builder.Property(d => d.InstalledAppsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(d => d.ComplianceViolationsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(d => d.PushToken).HasMaxLength(512);
        builder.Property(d => d.PushMagic).HasMaxLength(256);
        builder.Property(d => d.PushTopic).HasMaxLength(256);
        builder.Property(d => d.ProtectedUnlockToken).HasMaxLength(4096);
        builder.Property(d => d.IdentityThumbprint).HasMaxLength(128);
        builder.Ignore(d => d.Scenario);
        builder.Ignore(d => d.IsActive);
        builder.Ignore(d => d.InstalledApps);
        builder.Ignore(d => d.ComplianceViolations);

        builder.HasAlternateKey(d => new { d.TenantId, d.Id });
        builder.HasOne<MobilePolicy>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.PolicyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The provider id is globally unique (Android resource names embed the enterprise id;
        // Apple handles are random), which lets the device protocol find its tenant.
        builder.HasIndex(d => new { d.Platform, d.ProviderDeviceId }).IsUnique();
        builder.HasIndex(d => new { d.TenantId, d.State });
        builder.HasIndex(d => new { d.TenantId, d.Compliance });
    }
}

internal sealed class DeviceCommandConfiguration : IEntityTypeConfiguration<DeviceCommand>
{
    public void Configure(EntityTypeBuilder<DeviceCommand> builder)
    {
        builder.ToTable("mobile_device_commands");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Type).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(c => c.State).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(c => c.Reason).HasMaxLength(DeviceCommand.ReasonMaxLength);
        builder.Property(c => c.DecisionNote).HasMaxLength(DeviceCommand.ReasonMaxLength);
        builder.Property(c => c.Error).HasMaxLength(DeviceCommand.ErrorMaxLength);
        builder.Property(c => c.ProviderReference).HasMaxLength(512);
        builder.Property(c => c.ParametersJson).HasColumnType("jsonb");
        builder.Ignore(c => c.RequiresApproval);
        builder.Ignore(c => c.IsTerminal);

        builder.HasOne<MobileDevice>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.DeviceId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.TenantId, c.DeviceId, c.State });
        builder.HasIndex(c => new { c.TenantId, c.State });
    }
}

internal sealed class DeviceEventConfiguration : IEntityTypeConfiguration<DeviceEvent>
{
    public void Configure(EntityTypeBuilder<DeviceEvent> builder)
    {
        builder.ToTable("mobile_device_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(500).IsRequired();

        builder.HasOne<MobileDevice>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.DeviceId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.TenantId, e.DeviceId, e.CreatedAtUtc });
    }
}

internal sealed class MobileEnterpriseConfiguration : IEntityTypeConfiguration<MobileEnterprise>
{
    public void Configure(EntityTypeBuilder<MobileEnterprise> builder)
    {
        builder.ToTable("mobile_enterprises");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Platform).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.PendingSignupUrlName).HasMaxLength(512);
        builder.Property(e => e.EnterpriseName).HasMaxLength(256);
        builder.Property(e => e.DisplayName).HasMaxLength(256);
        builder.Ignore(e => e.IsBound);

        builder.HasIndex(e => new { e.TenantId, e.Platform }).IsUnique();
        builder.HasIndex(e => e.EnterpriseName).IsUnique().HasFilter("enterprise_name IS NOT NULL");
    }
}
