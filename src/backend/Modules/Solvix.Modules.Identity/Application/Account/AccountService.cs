namespace Solvix.Modules.Identity.Application.Account;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Application.Emails;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Application.Sessions;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IAccountService
{
    Task VerifyEmailAsync(VerifyEmailRequest request, CancellationToken cancellationToken);

    /// <summary>Always completes the same way, whether or not the address has an unverified account.</summary>
    Task ResendVerificationAsync(EmailRequest request, CancellationToken cancellationToken);

    /// <summary>Always completes the same way, whether or not the address has an account.</summary>
    Task RequestPasswordResetAsync(EmailRequest request, CancellationToken cancellationToken);

    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);

    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken);

    PasswordRulesDto GetBaselinePasswordRules();

    Task<PasswordRulesDto> GetMyPasswordRulesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Self-service operations on the caller's own credentials. Credentials belong
/// to the platform-level user, so these run in platform scope; nothing here is
/// reachable by a tenant administrator on behalf of someone else.
/// </summary>
internal sealed class AccountService(
    ISolvixDbContext db,
    IPasswordService passwords,
    ILoginAttemptTracker attempts,
    IOneTimeTokenService oneTimeTokens,
    ISessionManager sessions,
    IAuthAuditLog audit,
    IAuthEmails emails,
    ITenantContextMutator tenantContextMutator,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<IdentitySecurityOptions> options) : IAccountService
{
    private const string InvalidTokenMessage = "This link is no longer valid. Request a new one.";

    private readonly IdentitySecurityOptions _options = options.Value;

    public async Task VerifyEmailAsync(VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var token = await oneTimeTokens.FindUsableAsync(request.Token, UserTokenPurpose.EmailVerification, cancellationToken);
        if (token?.User is not { } user || !await oneTimeTokens.TryConsumeAsync(token, cancellationToken))
        {
            throw new ValidationException("token", InvalidTokenMessage);
        }

        user.MarkEmailVerified(clock.UtcNow);
        await audit.RecordForUserAsync(AuditEvents.EmailVerified, AuditOutcome.Success, user, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ResendVerificationAsync(EmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var normalized = User.NormalizeEmail(request.Email);
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (user is not { IsActive: true, IsEmailVerified: false }
            || await IsCoolingDownAsync(user.Id, UserTokenPurpose.EmailVerification, cancellationToken))
        {
            return;
        }

        var token = await oneTimeTokens.IssueAsync(
            user.Id, UserTokenPurpose.EmailVerification, TimeSpan.FromHours(_options.EmailVerificationTokenHours), null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        emails.SendEmailVerification(user.Email, user.DisplayName, token);
    }

    public async Task RequestPasswordResetAsync(EmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var normalized = User.NormalizeEmail(request.Email);
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (user is not { IsActive: true })
        {
            audit.Record(AuditEvents.PasswordResetRequested, AuditOutcome.Failure, tenantId: null, email: request.Email, detail: "unknown_account");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (await IsCoolingDownAsync(user.Id, UserTokenPurpose.PasswordReset, cancellationToken))
        {
            return;
        }

        var token = await oneTimeTokens.IssueAsync(
            user.Id, UserTokenPurpose.PasswordReset, TimeSpan.FromMinutes(_options.PasswordResetTokenMinutes), null, cancellationToken);
        await audit.RecordForUserAsync(AuditEvents.PasswordResetRequested, AuditOutcome.Success, user, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        emails.SendPasswordReset(user.Email, user.DisplayName, token);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var token = await oneTimeTokens.FindUsableAsync(request.Token, UserTokenPurpose.PasswordReset, cancellationToken);
        if (token?.User is not { IsActive: true } user)
        {
            throw new ValidationException("token", InvalidTokenMessage);
        }

        // Validate first so a rejected password does not burn the link.
        await passwords.ChangeAsync(user, request.NewPassword, "newPassword", additionalTenantId: null, cancellationToken);
        if (!await oneTimeTokens.TryConsumeAsync(token, cancellationToken))
        {
            throw new ValidationException("token", InvalidTokenMessage);
        }

        // Whoever holds the link controls the mailbox: that also verifies the address and lifts a lockout.
        // It does NOT bypass MFA: the next sign-in still asks for the second factor.
        user.MarkEmailVerified(clock.UtcNow);
        await attempts.ClearAsync(user.Id, cancellationToken);
        var revoked = await sessions.RevokeAllAsync(user.Id, tenantId: null, exceptSessionId: null, SessionRevocationReasons.PasswordReset, cancellationToken);
        await audit.RecordForUserAsync(
            AuditEvents.PasswordResetCompleted, AuditOutcome.Success, user, cancellationToken, detail: $"sessions_revoked={revoked}");
        await db.SaveChangesAsync(cancellationToken);
        emails.SendPasswordChanged(user.Email, user.DisplayName);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = currentUser.RequireUserId();
        var sessionId = currentUser.RequireSessionId();
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new UnauthorizedException();

        // The current password is a credential check like any other: it counts towards lockout.
        if (!await attempts.TryBeginAsync(user.Id, cancellationToken))
        {
            throw new ValidationException("currentPassword", "The current password is not correct.");
        }

        if (!passwords.Verify(user, request.CurrentPassword))
        {
            var lockedUntil = await attempts.RecordFailureAsync(user.Id, cancellationToken);
            await audit.RecordForUserAsync(AuditEvents.PasswordChanged, AuditOutcome.Failure, user, cancellationToken, sessionId, "wrong_current_password");
            if (lockedUntil is { } until)
            {
                await audit.RecordForUserAsync(AuditEvents.AccountLocked, AuditOutcome.Failure, user, cancellationToken, detail: $"until={until:O}");
                emails.SendAccountLocked(user.Email, user.DisplayName, until);
            }

            await db.SaveChangesAsync(cancellationToken);
            throw new ValidationException("currentPassword", "The current password is not correct.");
        }

        await attempts.ReleaseAsync(user.Id, cancellationToken);
        await passwords.ChangeAsync(user, request.NewPassword, "newPassword", additionalTenantId: null, cancellationToken);

        // Every other session ends; the one making the change stays signed in.
        var revoked = await sessions.RevokeAllAsync(user.Id, tenantId: null, sessionId, SessionRevocationReasons.PasswordChanged, cancellationToken);
        await audit.RecordForUserAsync(
            AuditEvents.PasswordChanged, AuditOutcome.Success, user, cancellationToken, sessionId, $"sessions_revoked={revoked}");
        await db.SaveChangesAsync(cancellationToken);
        emails.SendPasswordChanged(user.Email, user.DisplayName);
    }

    public PasswordRulesDto GetBaselinePasswordRules() => ToDto(PasswordRules.From(_options.Baseline()));

    public async Task<PasswordRulesDto> GetMyPasswordRulesAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        using var platformScope = tenantContextMutator.BeginPlatformScope();
        return ToDto(await passwords.GetRulesAsync(userId, additionalTenantId: null, cancellationToken));
    }

    internal static PasswordRulesDto ToDto(PasswordRules rules) => new(
        rules.MinLength,
        PasswordRules.MaxLength,
        rules.RequireUppercase,
        rules.RequireLowercase,
        rules.RequireDigit,
        rules.RequireSymbol,
        rules.HistoryCount);

    /// <summary>Stops an address from being flooded with mail by repeated requests.</summary>
    private async Task<bool> IsCoolingDownAsync(Guid userId, UserTokenPurpose purpose, CancellationToken cancellationToken)
    {
        var last = await oneTimeTokens.LastIssuedAtAsync(userId, purpose, cancellationToken);
        return last is { } issuedAt && clock.UtcNow - issuedAt < TimeSpan.FromSeconds(_options.EmailCooldownSeconds);
    }
}
