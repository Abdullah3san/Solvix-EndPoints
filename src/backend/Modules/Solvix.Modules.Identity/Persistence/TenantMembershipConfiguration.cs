namespace Solvix.Modules.Identity.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Identity.Domain;

internal sealed class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> builder)
    {
        builder.ToTable("tenant_memberships");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.IsActive).IsRequired();

        // Lets role assignments reference (tenant_id, id): a role can only ever be
        // attached to a membership of the same tenant, guaranteed by the database.
        builder.HasAlternateKey(m => new { m.TenantId, m.Id });

        builder.HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(m => m.Roles)
            .WithOne()
            .HasForeignKey(r => new { r.TenantId, r.MembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(m => m.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique();
        builder.HasIndex(m => m.UserId);
    }
}

internal sealed class MembershipRoleConfiguration : IEntityTypeConfiguration<MembershipRole>
{
    public void Configure(EntityTypeBuilder<MembershipRole> builder)
    {
        builder.ToTable("membership_roles");
        builder.HasKey(r => r.Id);

        // A role that is still assigned cannot be deleted.
        builder.HasOne(r => r.Role)
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.RoleId })
            .HasPrincipalKey(role => new { role.TenantId, role.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.TenantId, r.MembershipId, r.RoleId }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.RoleId });
    }
}
