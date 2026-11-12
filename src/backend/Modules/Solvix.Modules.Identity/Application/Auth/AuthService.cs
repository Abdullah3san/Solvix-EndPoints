namespace Solvix.Modules.Identity.Application.Auth;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Application.Emails;
using Solvix.Modules.Identity.Application.Mfa;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Application.Sessions;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    Task<AuthResponse> VerifyMfaAsync(MfaVerifyRequest request, CancellationToken cancellationToken);

    Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken);

    /// <summary>Ends the session identified by the access token and/or the refresh token. Never fails.</summary>
    Task LogoutAsync(Guid? sessionId, Guid? userId, string? refreshToken, CancellationToken cancellationToken);

    Task<AuthResponse> SwitchTenantAsync(string tenantSlug, CancellationToken cancellationToken);

    Task<CurrentUserDto> GetCurrentAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AuthTenantDto>> ListWorkspacesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Sign-in, token refresh, sign-out and tenant switching. These operations run
/// before a tenant scope exists (or deliberately across tenants, since a user
/// may belong to several), so they execute inside an explicit platform scope.
///
/// Failure responses for a wrong password, an unknown address, a locked account
/// and a disabled account are identical, and take the same time, so the
/// endpoint cannot be used to find out which addresses have accounts.
/// </summary>
internal sealed partial class AuthService(
    ISolvixDbContext db,
    IPasswordService passwords,
    ILoginAttemptTracker attempts,
    IOneTimeTokenService oneTimeTokens,
    ISessionManager sessions,
    ISecretProtector protector,
    IAuthAuditLog audit,
    IAuthEmails emails,
    ITenantDirectory tenantDirectory,
    ITenantContextMutator tenantContextMutator,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IAccessContext accessContext,
    IClock clock,
    IOptions<IdentitySecurityOptions> options,
    ILogger<AuthService> logger) : IAuthService
{
    private const string InvalidCredentialsCode = "invalid_credentials";
    private const string InvalidCredentialsMessage = "Invalid e-mail or password.";

    private readonly IdentitySecurityOptions _options = options.Value;

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var normalized = User.NormalizeEmail(request.Email);
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);

        if (user is null)
        {
            passwords.VerifyDummy(request.Password);
            audit.Record(AuditEvents.LoginFailed, AuditOutcome.Failure, tenantId: null, email: request.Email, detail: "unknown_account");
            await db.SaveChangesAsync(cancellationToken);
            throw InvalidCredentials(normalized);
        }

        // Reserve one of the remaining attempts BEFORE looking at the password.
        if (!await attempts.TryBeginAsync(user.Id, cancellationToken))
        {
            passwords.VerifyDummy(request.Password);
            await audit.RecordForUserAsync(AuditEvents.LoginFailed, AuditOutcome.Failure, user, cancellationToken, detail: "locked_out");
            await db.SaveChangesAsync(cancellationToken);
            throw InvalidCredentials(normalized);
        }

        if (!passwords.Verify(user, request.Password))
        {
            await FailAttemptAsync(user, AuditEvents.LoginFailed, "wrong_password", cancellationToken);
            throw InvalidCredentials(normalized);
        }

        // From here on the caller has proven knowledge of the password, so specific reasons may be given.
        if (!user.IsActive)
        {
            await attempts.ReleaseAsync(user.Id, cancellationToken);
            await audit.RecordForUserAsync(AuditEvents.LoginFailed, AuditOutcome.Failure, user, cancellationToken, detail: "account_disabled");
            await db.SaveChangesAsync(cancellationToken);
            throw InvalidCredentials(normalized);
        }

        if (_options.RequireEmailVerification && !user.IsEmailVerified)
        {
            await attempts.ReleaseAsync(user.Id, cancellationToken);
            await audit.RecordForUserAsync(AuditEvents.LoginFailed, AuditOutcome.Failure, user, cancellationToken, detail: "email_not_verified");
            await db.SaveChangesAsync(cancellationToken);
            throw new ForbiddenException("Confirm your e-mail address before signing in. Check your inbox for the confirmation link.", "email_not_verified");
        }

        var rules = await passwords.GetRulesAsync(user.Id, additionalTenantId: null, cancellationToken);
        if (user.IsPasswordExpired(clock.UtcNow, rules.MaxAgeDays))
        {
            await attempts.ReleaseAsync(user.Id, cancellationToken);
            await audit.RecordForUserAsync(AuditEvents.LoginFailed, AuditOutcome.Failure, user, cancellationToken, detail: "password_expired");
            await db.SaveChangesAsync(cancellationToken);
            throw new ForbiddenException("Your password has expired. Use \"Forgot password\" to choose a new one.", "password_expired");
        }

        var memberships = await LoadWorkspacesAsync(user.Id, cancellationToken);
        if (memberships.Count == 0)
        {
            await attempts.ReleaseAsync(user.Id, cancellationToken);
            audit.Record(AuditEvents.LoginFailed, AuditOutcome.Failure, tenantId: null, user.Id, user.Email, detail: "no_active_workspace");
            await db.SaveChangesAsync(cancellationToken);
            throw new ForbiddenException("This account is not a member of any active workspace.", "no_active_workspace");
        }

        var tenant = SelectTenant(memberships, request.TenantSlug);
        if (tenant is null)
        {
            // A token is only ever issued for a workspace the user belongs to; the slug cannot widen that.
            await attempts.ReleaseAsync(user.Id, cancellationToken);
            await audit.RecordForUserAsync(AuditEvents.LoginFailed, AuditOutcome.Failure, user, cancellationToken, detail: "not_a_member_of_requested_workspace");
            await db.SaveChangesAsync(cancellationToken);
            throw new ForbiddenException("You are not a member of the requested workspace.");
        }

        if (user.MfaEnabled)
        {
            // Password accepted, second factor outstanding: no session and no tokens yet.
            await attempts.ReleaseAsync(user.Id, cancellationToken);
            var lifetime = TimeSpan.FromMinutes(_options.MfaChallengeMinutes);
            var mfaToken = await oneTimeTokens.IssueAsync(user.Id, UserTokenPurpose.MfaChallenge, lifetime, tenant.Id.ToString(), cancellationToken);
            audit.Record(AuditEvents.MfaChallengeIssued, AuditOutcome.Success, tenant.Id, user.Id, user.Email);
            await db.SaveChangesAsync(cancellationToken);
            return new LoginResponse(true, null, new MfaChallengeDto(mfaToken, clock.UtcNow.Add(lifetime)));
        }

        return new LoginResponse(false, await CompleteSignInAsync(user, tenant, mfaVerified: false, cancellationToken), null);
    }

    public async Task<AuthResponse> VerifyMfaAsync(MfaVerifyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var challenge = await oneTimeTokens.FindUsableAsync(request.MfaToken, UserTokenPurpose.MfaChallenge, cancellationToken);
        if (challenge?.User is not { MfaEnabled: true, MfaSecret: not null, IsActive: true } user
            || !Guid.TryParse(challenge.Data, out var tenantId))
        {
            throw new UnauthorizedException("This sign-in attempt has expired. Sign in again.", "mfa_challenge_invalid");
        }

        if (!await attempts.TryBeginAsync(user.Id, cancellationToken))
        {
            await oneTimeTokens.TryConsumeAsync(challenge, cancellationToken);
            await audit.RecordForUserAsync(AuditEvents.MfaVerificationFailed, AuditOutcome.Failure, user, cancellationToken, detail: "locked_out");
            await db.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedException("This sign-in attempt has expired. Sign in again.", "mfa_challenge_invalid");
        }

        var verified = false;
        var usedRecoveryCode = !string.IsNullOrWhiteSpace(request.RecoveryCode) && string.IsNullOrWhiteSpace(request.Code);
        if (!usedRecoveryCode)
        {
            var step = Totp.Verify(protector.Unprotect(user.MfaSecret!), request.Code, clock.UtcNow, user.MfaLastUsedStep);
            if (step is { } matched)
            {
                // Atomic claim of the time step: the same code can never be accepted twice, even by parallel requests.
                var claimed = await db.Set<User>()
                    .Where(u => u.Id == user.Id && (u.MfaLastUsedStep == null || u.MfaLastUsedStep < matched))
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.MfaLastUsedStep, (long?)matched), cancellationToken);
                verified = claimed == 1;
            }
        }
        else if (RecoveryCodes.Normalize(request.RecoveryCode) is { } recovery)
        {
            var hash = protector.KeyedHash(recovery);
            var now = clock.UtcNow;

            // Atomic single use: only one request can flip used_at_utc from NULL.
            var used = await db.Set<UserRecoveryCode>()
                .Where(c => c.UserId == user.Id && c.CodeHash == hash && c.UsedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAtUtc, (DateTimeOffset?)now), cancellationToken);
            verified = used == 1;
        }

        if (!verified)
        {
            var failures = await oneTimeTokens.RegisterFailedAttemptAsync(challenge, cancellationToken);
            if (failures >= _options.MfaChallengeMaxAttempts)
            {
                await oneTimeTokens.TryConsumeAsync(challenge, cancellationToken);
            }

            await FailAttemptAsync(user, AuditEvents.MfaVerificationFailed, usedRecoveryCode ? "wrong_recovery_code" : "wrong_code", cancellationToken);
            throw new UnauthorizedException("The code is not valid. Try again with a new code.", "mfa_code_invalid");
        }

        // The challenge is spent whether or not the rest succeeds; a parallel request with the same token loses here.
        if (!await oneTimeTokens.TryConsumeAsync(challenge, cancellationToken))
        {
            throw new UnauthorizedException("This sign-in attempt has expired. Sign in again.", "mfa_challenge_invalid");
        }

        var memberships = await LoadWorkspacesAsync(user.Id, cancellationToken);
        var tenant = memberships.FirstOrDefault(t => t.Id == tenantId)
            ?? throw new ForbiddenException("You are no longer a member of that workspace.", "no_active_workspace");

        if (usedRecoveryCode)
        {
            audit.Record(AuditEvents.MfaRecoveryCodeUsed, AuditOutcome.Success, tenant.Id, user.Id, user.Email);
        }

        return await CompleteSignInAsync(user, tenant, mfaVerified: true, cancellationToken);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        var outcome = await sessions.RotateAsync(request.RefreshToken, cancellationToken);
        if (outcome.Failure == RefreshFailure.ReuseDetected && outcome.Session is { } compromised)
        {
            LogRefreshTokenReuse(logger, compromised.Id, compromised.UserId);
            audit.Record(
                AuditEvents.RefreshTokenReuseDetected,
                AuditOutcome.Failure,
                compromised.TenantId,
                compromised.UserId,
                outcome.User?.Email,
                compromised.Id,
                "session_revoked");
        }

        // Persists the rotation, or the revocation that a failed rotation caused.
        await db.SaveChangesAsync(cancellationToken);

        if (outcome.Failure != RefreshFailure.None || outcome.Issued is null || outcome.User is null || outcome.Tenant is null)
        {
            throw new UnauthorizedException("Your session has ended. Sign in again.", "invalid_refresh_token");
        }

        return ToResponse(outcome.User, outcome.Tenant, outcome.Issued);
    }

    public async Task LogoutAsync(Guid? sessionId, Guid? userId, string? refreshToken, CancellationToken cancellationToken)
    {
        using var platformScope = tenantContextMutator.BeginPlatformScope();

        UserSession? session = null;
        if (sessionId is { } sid && userId is { } uid)
        {
            session = await db.Set<UserSession>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Id == sid && s.UserId == uid, cancellationToken);
        }

        session ??= await sessions.FindByRefreshTokenAsync(refreshToken, cancellationToken);
        if (session is null)
        {
            return;
        }

        if (await sessions.RevokeAsync(session, SessionRevocationReasons.SignedOut, cancellationToken))
        {
            audit.Record(AuditEvents.Logout, AuditOutcome.Success, session.TenantId, session.UserId, sessionId: session.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuthResponse> SwitchTenantAsync(string tenantSlug, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantSlug);
        var userId = currentUser.RequireUserId();
        var currentSessionId = currentUser.RequireSessionId();
        var mfaVerified = accessContext.Grant?.MfaVerified == true;

        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken)
            ?? throw new UnauthorizedException();

        // Only workspaces the caller is an active member of are candidates; the slug merely picks one of them.
        var slug = tenantSlug.Trim().ToLowerInvariant();
        var memberships = await LoadWorkspacesAsync(user.Id, cancellationToken);
        var target = memberships.FirstOrDefault(t => t.Slug == slug)
            ?? throw new ForbiddenException("You are not a member of that workspace.");

        var current = await db.Set<UserSession>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == currentSessionId && s.UserId == userId, cancellationToken);
        if (current is not null)
        {
            await sessions.RevokeAsync(current, SessionRevocationReasons.TenantSwitched, cancellationToken);
        }

        var issued = await sessions.StartAsync(user, target, mfaVerified, cancellationToken);
        audit.Record(AuditEvents.TenantSwitched, AuditOutcome.Success, target.Id, user.Id, user.Email, issued.Session.Id);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(user, target, issued);
    }

    public async Task<CurrentUserDto> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var tenantId = tenantContext.RequireTenantId();
        var grant = accessContext.Grant ?? throw new UnauthorizedException();

        var user = await db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new UnauthorizedException();
        var tenant = await tenantDirectory.FindByIdAsync(tenantId, cancellationToken)
            ?? throw new NotFoundException("Tenant", tenantId);

        return new CurrentUserDto(
            new UserDto(user.Id, user.Email, user.DisplayName),
            new AuthTenantDto(tenant.Id, tenant.Slug, tenant.Name),
            grant.SessionId,
            grant.Roles,
            [.. grant.Permissions.Order(StringComparer.Ordinal)],
            user.MfaEnabled,
            grant.MfaVerified,
            grant.MfaEnrollmentRequired);
    }

    public async Task<IReadOnlyList<AuthTenantDto>> ListWorkspacesAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var workspaces = await LoadWorkspacesAsync(userId, cancellationToken);
        return [.. workspaces.Select(t => new AuthTenantDto(t.Id, t.Slug, t.Name))];
    }

    private async Task<AuthResponse> CompleteSignInAsync(User user, TenantDescriptor tenant, bool mfaVerified, CancellationToken cancellationToken)
    {
        await attempts.RecordSuccessAsync(user.Id, cancellationToken);
        var issued = await sessions.StartAsync(user, tenant, mfaVerified, cancellationToken);
        audit.Record(
            AuditEvents.LoginSucceeded,
            AuditOutcome.Success,
            tenant.Id,
            user.Id,
            user.Email,
            issued.Session.Id,
            mfaVerified ? "mfa" : "password");
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(user, tenant, issued);
    }

    private async Task FailAttemptAsync(User user, string eventType, string reason, CancellationToken cancellationToken)
    {
        var lockedUntil = await attempts.RecordFailureAsync(user.Id, cancellationToken);
        await audit.RecordForUserAsync(eventType, AuditOutcome.Failure, user, cancellationToken, detail: reason);
        if (lockedUntil is { } until)
        {
            await audit.RecordForUserAsync(AuditEvents.AccountLocked, AuditOutcome.Failure, user, cancellationToken, detail: $"until={until:O}");
            emails.SendAccountLocked(user.Email, user.DisplayName, until);
            LogAccountLocked(logger, user.Id, until);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The active tenants the user is an active member of, oldest membership first.</summary>
    private async Task<List<TenantDescriptor>> LoadWorkspacesAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Platform scope: the tenant query filter is explicitly lifted because we
        // are enumerating ALL tenants this user belongs to.
        var tenantIds = await db.Set<TenantMembership>()
            .IgnoreQueryFilters()
            .Where(m => m.UserId == userId && m.IsActive)
            .OrderBy(m => m.CreatedAtUtc)
            .Select(m => m.TenantId)
            .ToListAsync(cancellationToken);

        var resolved = new List<TenantDescriptor>(tenantIds.Count);
        foreach (var tenantId in tenantIds)
        {
            var tenant = await tenantDirectory.FindByIdAsync(tenantId, cancellationToken);
            if (tenant is { IsActive: true })
            {
                resolved.Add(tenant);
            }
        }

        return resolved;
    }

    private static TenantDescriptor? SelectTenant(List<TenantDescriptor> memberships, string? requestedSlug)
    {
        if (string.IsNullOrWhiteSpace(requestedSlug))
        {
            return memberships[0];
        }

        var slug = requestedSlug.Trim().ToLowerInvariant();
        return memberships.FirstOrDefault(t => t.Slug == slug);
    }

    internal static AuthResponse ToResponse(User user, TenantDescriptor tenant, IssuedSession issued) =>
        new(
            issued.AccessToken,
            issued.AccessTokenExpiresAtUtc,
            issued.RefreshToken,
            issued.RefreshTokenExpiresAtUtc,
            issued.Session.Id,
            new UserDto(user.Id, user.Email, user.DisplayName),
            new AuthTenantDto(tenant.Id, tenant.Slug, tenant.Name));

    private UnauthorizedException InvalidCredentials(string normalizedEmail)
    {
        LogFailedLogin(logger, normalizedEmail);
        return new UnauthorizedException(InvalidCredentialsMessage, InvalidCredentialsCode);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed login attempt for {NormalizedEmail}")]
    private static partial void LogFailedLogin(ILogger logger, string normalizedEmail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account {UserId} locked until {LockedUntil} after repeated failed sign-ins")]
    private static partial void LogAccountLocked(ILogger logger, Guid userId, DateTimeOffset lockedUntil);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh token reuse detected: session {SessionId} of user {UserId} revoked")]
    private static partial void LogRefreshTokenReuse(ILogger logger, Guid sessionId, Guid userId);
}
