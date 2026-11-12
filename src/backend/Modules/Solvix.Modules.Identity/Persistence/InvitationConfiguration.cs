namespace Solvix.Modules.Identity.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Identity.Domain;

internal sealed class UserInvitationConfiguration : IEntityTypeConfiguration<UserInvitation>
{
    public void Configure(EntityTypeBuilder<UserInvitation> builder)
    {
        builder.ToTable("user_invitations");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(i => i.NormalizedEmail).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(i => i.DisplayName).HasMaxLength(User.DisplayNameMaxLength);
        builder.Property(i => i.TokenHash).HasMaxLength(UserInvitation.TokenHashLength).IsFixedLength().IsRequired();
        builder.Ignore(i => i.IsPending);

        builder.HasAlternateKey(i => new { i.TenantId, i.Id });

        builder.HasOne<User>().WithMany().HasForeignKey(i => i.InvitedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(i => i.Roles)
            .WithOne()
            .HasForeignKey(r => new { r.TenantId, r.InvitationId })
            .HasPrincipalKey(i => new { i.TenantId, i.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(i => i.TokenHash).IsUnique();

        // One open invitation per address per tenant.
        builder.HasIndex(i => new { i.TenantId, i.NormalizedEmail })
            .HasDatabaseName("ix_user_invitations_tenant_pending_email")
            .IsUnique()
            .HasFilter("accepted_at_utc IS NULL AND revoked_at_utc IS NULL");
    }
}

internal sealed class UserInvitationRoleConfiguration : IEntityTypeConfiguration<UserInvitationRole>
{
    public void Configure(EntityTypeBuilder<UserInvitationRole> builder)
    {
        builder.ToTable("user_invitation_roles");
        builder.HasKey(r => r.Id);

        builder.HasOne(r => r.Role)
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.RoleId })
            .HasPrincipalKey(role => new { role.TenantId, role.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.TenantId, r.InvitationId, r.RoleId }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.RoleId });
    }
}
