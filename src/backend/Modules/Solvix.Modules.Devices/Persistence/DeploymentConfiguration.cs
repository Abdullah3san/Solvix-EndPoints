namespace Solvix.Modules.Devices.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Devices.Domain;

internal sealed class DeploymentProfileConfiguration : IEntityTypeConfiguration<DeploymentProfile>
{
    public void Configure(EntityTypeBuilder<DeploymentProfile> builder)
    {
        builder.ToTable("deployment_profiles");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).HasMaxLength(DeploymentProfile.NameMaxLength).IsRequired();
        builder.Property(p => p.NormalizedName).HasMaxLength(DeploymentProfile.NameMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(DeploymentProfile.DescriptionMaxLength);
        builder.Property(p => p.CodeSecret).HasMaxLength(DeploymentProfile.SecretLength).IsRequired();

        builder.HasAlternateKey(p => new { p.TenantId, p.Id });
        builder.HasIndex(p => new { p.TenantId, p.NormalizedName }).IsUnique();

        // BranchId / DepartmentId / DeviceGroupId reference other modules' tables; their tenant-safe composite
        // foreign keys (ON DELETE SET NULL) are declared in the DeploymentEnrollmentSecurity migration.
    }
}

internal sealed class PendingEnrollmentConfiguration : IEntityTypeConfiguration<PendingEnrollment>
{
    public void Configure(EntityTypeBuilder<PendingEnrollment> builder)
    {
        builder.ToTable("pending_enrollments");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.Hostname).HasMaxLength(PendingEnrollment.HostnameMaxLength).IsRequired();
        builder.Property(p => p.IdentityJson).HasColumnType("jsonb").IsRequired();
        builder.Property(p => p.HardwareFingerprint).HasMaxLength(Device.FingerprintLength);
        builder.Property(p => p.PublicKeySpki).IsRequired();
        builder.Property(p => p.PollSecretHash).HasMaxLength(32).IsRequired();
        builder.Property(p => p.SourceAddress).HasMaxLength(PendingEnrollment.AddressMaxLength);
        builder.Property(p => p.DecisionNote).HasMaxLength(PendingEnrollment.NoteMaxLength);

        // Deleting a profile discards the requests made through it.
        builder.HasOne<DeploymentProfile>()
            .WithMany()
            .HasForeignKey(p => new { p.TenantId, p.ProfileId })
            .HasPrincipalKey(profile => new { profile.TenantId, profile.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => new { p.TenantId, p.Status, p.CreatedAtUtc });
        builder.HasIndex(p => new { p.TenantId, p.HardwareFingerprint });

        // ExistingDeviceId / DeviceId reference devices; SET NULL (column) FKs are in the security migration.
    }
}
