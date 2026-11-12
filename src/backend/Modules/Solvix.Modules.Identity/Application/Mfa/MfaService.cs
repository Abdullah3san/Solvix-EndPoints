namespace Solvix.Modules.Identity.Application.Mfa;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Application.Access;
using Solvix.Modules.Identity.Application.Account;
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

public interface IMfaService
{
    Task<MfaStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Starts (or restarts) authenticator enrolment after re-checking the password.</summary>
    Task<MfaSetupDto> BeginSetupAsync(PasswordConfirmationRequest request, CancellationToken cancellationToken);

    /// <summary>Activates MFA once the authenticator produces a valid code. Returns the recovery codes.</summary>
    Task<RecoveryCodesDto> ConfirmSetupAsync(MfaConfirmRequest request, CancellationToken cancellationToken);

    Task DisableAsync(MfaProtectedRequest request, CancellationToken cancellationToken);

    Task<RecoveryCodesDto> RegenerateRecoveryCodesAsync(MfaProtectedRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Self-service multi-factor authentication (TOTP authenticator apps plus
/// recovery codes). Every change to the second factor requires the password
/// again, and removing it also requires a valid code, so a hijacked browser
/// session alone cannot take the protection away.
/// </summary>
internal sealed class MfaService(
    ISolvixDbContext db,
    IPasswordService passwords,
    ILoginAttemptTracker attempts,
    ISecretProtector protector,
    ISessionManager sessions,
    ISecurityPolicyReader policies,
    IAccessCache accessCache,
    IAuthAuditLog audit,
    IAuthEmails emails,
    ITenantContextMutator tenantContextMutator,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<IdentitySecurityOptions> options) : IMfaService
{
    private readonly IdentitySecurityOptions _options = options.Value;

    public async Task<MfaStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var policy = await policies.FindAsync(tenantContext.RequireTenantId(), cancellationToken);
        var user = await db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new UnauthorizedException();
        var remaining = await db.Set<UserRecoveryCode>().CountAsync(c => c.UserId == userId && c.UsedAtUtc == null, cancellationToken);
        return new MfaStatusDto(user.MfaEnabled, user.MfaEnabledAtUtc, user.MfaEnabled ? remaining : 0, policy?.RequireMfa == true);
    }

    public async Task<MfaSetupDto> BeginSetupAsync(PasswordConfirmationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var user = await LoadUserAsync(cancellationToken);
        await RequirePasswordAsync(user, request.Password, cancellationToken);

        if (user.MfaEnabled)
        {
            throw new ConflictException("Multi-factor authentication is already turned on. Turn it off first to enrol a new authenticator.", "mfa_already_enabled");
        }

        var secret = Totp.GenerateSecret();
        user.BeginMfaEnrollment(protector.Protect(secret));
        await db.SaveChangesAsync(cancellationToken);
        return new MfaSetupDto(secret, Totp.BuildUri(_options.MfaIssuer, user.Email, secret));
    }

    public async Task<RecoveryCodesDto> ConfirmSetupAsync(MfaConfirmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sessionId = currentUser.RequireSessionId();
        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var user = await LoadUserAsync(cancellationToken);

        if (user.MfaEnabled || user.MfaPendingSecret is null)
        {
            throw new ConflictException("Start the authenticator set-up first.", "mfa_setup_not_started");
        }

        await RequireAttemptAsync(user, "code", cancellationToken);
        var step = Totp.Verify(protector.Unprotect(user.MfaPendingSecret), request.Code, clock.UtcNow, lastUsedStep: null);
        if (step is not { } matched)
        {
            throw await FailAsync(user, AuditEvents.MfaEnabled, "wrong_code", "code", "The code is not valid. Check the time on your phone and try again.", cancellationToken);
        }

        await attempts.ReleaseAsync(user.Id, cancellationToken);
        user.ConfirmMfaEnrollment(matched, clock.UtcNow);
        var codes = await ReplaceRecoveryCodesAsync(user.Id, cancellationToken);

        // The session that just proved possession of the authenticator counts as MFA-verified;
        // every other session is ended so nothing keeps running on a password alone.
        var current = await db.Set<UserSession>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == user.Id, cancellationToken);
        if (current is not null)
        {
            current.MarkMfaVerified();
            await accessCache.InvalidateSessionAsync(current.TenantId, current.Id, cancellationToken);
        }

        await sessions.RevokeAllAsync(user.Id, tenantId: null, sessionId, SessionRevocationReasons.MfaChanged, cancellationToken);
        await audit.RecordForUserAsync(AuditEvents.MfaEnabled, AuditOutcome.Success, user, cancellationToken, sessionId);
        await db.SaveChangesAsync(cancellationToken);
        emails.SendMfaChanged(user.Email, user.DisplayName, enabled: true);
        return new RecoveryCodesDto(codes);
    }

    public async Task DisableAsync(MfaProtectedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sessionId = currentUser.RequireSessionId();
        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var user = await LoadUserAsync(cancellationToken);
        await RequirePasswordAndCodeAsync(user, request, AuditEvents.MfaDisabled, cancellationToken);

        user.DisableMfa();
        await db.Set<UserRecoveryCode>().Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await sessions.RevokeAllAsync(user.Id, tenantId: null, sessionId, SessionRevocationReasons.MfaChanged, cancellationToken);
        await audit.RecordForUserAsync(AuditEvents.MfaDisabled, AuditOutcome.Success, user, cancellationToken, sessionId);
        await db.SaveChangesAsync(cancellationToken);
        emails.SendMfaChanged(user.Email, user.DisplayName, enabled: false);
    }

    public async Task<RecoveryCodesDto> RegenerateRecoveryCodesAsync(MfaProtectedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sessionId = currentUser.RequireSessionId();
        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var user = await LoadUserAsync(cancellationToken);
        await RequirePasswordAndCodeAsync(user, request, AuditEvents.MfaRecoveryCodesRegenerated, cancellationToken);

        var codes = await ReplaceRecoveryCodesAsync(user.Id, cancellationToken);
        await audit.RecordForUserAsync(AuditEvents.MfaRecoveryCodesRegenerated, AuditOutcome.Success, user, cancellationToken, sessionId);
        await db.SaveChangesAsync(cancellationToken);
        return new RecoveryCodesDto(codes);
    }

    private async Task<User> LoadUserAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        return await db.Set<User>().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken)
            ?? throw new UnauthorizedException();
    }

    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await db.Set<UserRecoveryCode>().Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        var codes = RecoveryCodes.Generate();
        foreach (var code in codes)
        {
            db.Set<UserRecoveryCode>().Add(UserRecoveryCode.Create(userId, protector.KeyedHash(RecoveryCodes.Normalize(code)!)));
        }

        return codes;
    }

    private async Task RequirePasswordAsync(User user, string password, CancellationToken cancellationToken)
    {
        await RequireAttemptAsync(user, "password", cancellationToken);
        if (!passwords.Verify(user, password))
        {
            throw await FailAsync(user, AuditEvents.MfaVerificationFailed, "wrong_password", "password", "The password is not correct.", cancellationToken);
        }

        await attempts.ReleaseAsync(user.Id, cancellationToken);
    }

    private async Task RequirePasswordAndCodeAsync(User user, MfaProtectedRequest request, string eventType, CancellationToken cancellationToken)
    {
        if (!user.MfaEnabled || user.MfaSecret is null)
        {
            throw new ConflictException("Multi-factor authentication is not turned on.", "mfa_not_enabled");
        }

        await RequirePasswordAsync(user, request.Password, cancellationToken);
        await RequireAttemptAsync(user, "code", cancellationToken);

        var verified = false;
        if (Totp.Verify(protector.Unprotect(user.MfaSecret), request.Code, clock.UtcNow, user.MfaLastUsedStep) is { } step)
        {
            user.RecordMfaStep(step);
            verified = true;
        }
        else if (RecoveryCodes.Normalize(request.Code) is { } recovery)
        {
            var hash = protector.KeyedHash(recovery);
            var now = clock.UtcNow;
            verified = await db.Set<UserRecoveryCode>()
                .Where(c => c.UserId == user.Id && c.CodeHash == hash && c.UsedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAtUtc, (DateTimeOffset?)now), cancellationToken) == 1;
        }

        if (!verified)
        {
            throw await FailAsync(user, eventType, "wrong_code", "code", "The code is not valid.", cancellationToken);
        }

        await attempts.ReleaseAsync(user.Id, cancellationToken);
    }

    private async Task RequireAttemptAsync(User user, string field, CancellationToken cancellationToken)
    {
        if (!await attempts.TryBeginAsync(user.Id, cancellationToken))
        {
            throw new ValidationException(field, "Too many failed attempts. Try again later.");
        }
    }

    /// <summary>Counts the failure (possibly locking the account), records it, and returns the error to throw.</summary>
    private async Task<ValidationException> FailAsync(User user, string eventType, string reason, string field, string message, CancellationToken cancellationToken)
    {
        var lockedUntil = await attempts.RecordFailureAsync(user.Id, cancellationToken);
        await audit.RecordForUserAsync(eventType, AuditOutcome.Failure, user, cancellationToken, currentUser.SessionId, reason);
        if (lockedUntil is { } until)
        {
            await audit.RecordForUserAsync(AuditEvents.AccountLocked, AuditOutcome.Failure, user, cancellationToken, detail: $"until={until:O}");
            emails.SendAccountLocked(user.Email, user.DisplayName, until);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ValidationException(field, message);
    }
}
