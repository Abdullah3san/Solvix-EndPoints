namespace Solvix.Modules.Identity.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Solvix.Modules.Identity.Domain;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(u => u.NormalizedEmail).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(User.DisplayNameMaxLength).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(u => u.IsActive).IsRequired();
        builder.Property(u => u.MfaSecret).HasMaxLength(512);
        builder.Property(u => u.MfaPendingSecret).HasMaxLength(512);
        builder.Ignore(u => u.IsEmailVerified);
        builder.HasIndex(u => u.NormalizedEmail).IsUnique();
    }
}

internal sealed class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
{
    public void Configure(EntityTypeBuilder<UserToken> builder)
    {
        builder.ToTable("user_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Purpose).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(t => t.TokenHash).HasMaxLength(UserToken.TokenHashLength).IsFixedLength().IsRequired();
        builder.Property(t => t.Data).HasMaxLength(UserToken.DataMaxLength);
        builder.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.UserId, t.Purpose });
    }
}

internal sealed class PasswordHistoryEntryConfiguration : IEntityTypeConfiguration<PasswordHistoryEntry>
{
    public void Configure(EntityTypeBuilder<PasswordHistoryEntry> builder)
    {
        builder.ToTable("user_password_history");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.PasswordHash).HasMaxLength(512).IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(h => h.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(h => new { h.UserId, h.CreatedAtUtc });
    }
}

internal sealed class UserRecoveryCodeConfiguration : IEntityTypeConfiguration<UserRecoveryCode>
{
    public void Configure(EntityTypeBuilder<UserRecoveryCode> builder)
    {
        builder.ToTable("user_recovery_codes");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.CodeHash).HasMaxLength(UserRecoveryCode.CodeHashLength).IsFixedLength().IsRequired();
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.UserId, c.CodeHash }).IsUnique();
    }
}
