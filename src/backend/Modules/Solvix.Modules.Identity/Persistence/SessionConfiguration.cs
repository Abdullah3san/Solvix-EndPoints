namespace Solvix.Modules.Identity.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Identity.Domain;

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.RevokedReason).HasMaxLength(UserSession.RevokedReasonMaxLength);
        builder.Property(s => s.IpAddress).HasMaxLength(UserSession.IpAddressMaxLength);
        builder.Property(s => s.UserAgent).HasMaxLength(UserSession.UserAgentMaxLength);

        builder.HasAlternateKey(s => new { s.TenantId, s.Id });

        builder.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.TenantId, s.UserId });
        builder.HasIndex(s => s.UserId);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.TokenHash).HasMaxLength(RefreshToken.TokenHashLength).IsFixedLength().IsRequired();

        builder.HasOne(t => t.Session)
            .WithMany()
            .HasForeignKey(t => new { t.TenantId, t.SessionId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.SessionId });
    }
}
