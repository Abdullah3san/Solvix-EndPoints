namespace Solvix.Modules.Identity.Domain;

using Solvix.SharedKernel.Entities;

public enum AuditOutcome
{
    Success = 1,
    Failure = 2,
}

/// <summary>
/// One immutable entry of the authentication and access audit trail. Rows are
/// append-only: the database rejects UPDATE and DELETE. A row belongs to the
/// tenant in <see cref="TenantId"/>; rows without a tenant (e.g. a failed
/// sign-in for an unknown address) are visible to the platform only.
/// </summary>
public sealed class AuthAuditEvent : ITenantVisible
{
    public const int EventTypeMaxLength = 64;
    public const int EmailMaxLength = 320;
    public const int IpAddressMaxLength = 45;
    public const int UserAgentMaxLength = 512;
    public const int CorrelationIdMaxLength = 128;
    public const int DetailMaxLength = 1024;

    private AuthAuditEvent()
    {
    }

    public AuthAuditEvent(
        DateTimeOffset occurredAtUtc,
        string eventType,
        AuditOutcome outcome,
        Guid? tenantId,
        Guid? userId,
        Guid? actorUserId,
        string? email,
        Guid? sessionId,
        string? ipAddress,
        string? userAgent,
        string? correlationId,
        string? detail)
    {
        OccurredAtUtc = occurredAtUtc;
        EventType = eventType;
        Outcome = outcome;
        TenantId = tenantId;
        UserId = userId;
        ActorUserId = actorUserId;
        Email = Truncate(email, EmailMaxLength);
        SessionId = sessionId;
        IpAddress = Truncate(ipAddress, IpAddressMaxLength);
        UserAgent = Truncate(userAgent, UserAgentMaxLength);
        CorrelationId = Truncate(correlationId, CorrelationIdMaxLength);
        Detail = Truncate(detail, DetailMaxLength);
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public AuditOutcome Outcome { get; private set; }

    public Guid? TenantId { get; private set; }

    /// <summary>The account the event is about.</summary>
    public Guid? UserId { get; private set; }

    /// <summary>Who caused the event, when that is someone other than <see cref="UserId"/> (administrative actions).</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>The e-mail address involved, kept so failed attempts for unknown accounts are still traceable.</summary>
    public string? Email { get; private set; }

    public Guid? SessionId { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>Short machine-oriented context such as a failure reason. Never contains secrets.</summary>
    public string? Detail { get; private set; }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}

/// <summary>Audit event names. Stable: they are part of the API and of exported logs.</summary>
public static class AuditEvents
{
    public const string TenantRegistered = "tenant.registered";
    public const string LoginSucceeded = "login.succeeded";
    public const string LoginFailed = "login.failed";
    public const string MfaChallengeIssued = "login.mfa_challenge";
    public const string MfaVerificationFailed = "mfa.verification_failed";
    public const string AccountLocked = "account.locked";
    public const string Logout = "logout";
    public const string TenantSwitched = "session.tenant_switched";
    public const string SessionRevoked = "session.revoked";
    public const string SessionsRevokedByAdministrator = "session.revoked_by_administrator";
    public const string RefreshTokenReuseDetected = "token.reuse_detected";
    public const string EmailVerified = "email.verified";
    public const string PasswordResetRequested = "password.reset_requested";
    public const string PasswordResetCompleted = "password.reset_completed";
    public const string PasswordChanged = "password.changed";
    public const string MfaEnabled = "mfa.enabled";
    public const string MfaDisabled = "mfa.disabled";
    public const string MfaRecoveryCodesRegenerated = "mfa.recovery_codes_regenerated";
    public const string MfaRecoveryCodeUsed = "mfa.recovery_code_used";
    public const string UserInvited = "user.invited";
    public const string InvitationRevoked = "invitation.revoked";
    public const string InvitationAccepted = "invitation.accepted";
    public const string UserActivated = "user.activated";
    public const string UserDeactivated = "user.deactivated";
    public const string UserRemoved = "user.removed";
    public const string UserRolesChanged = "user.roles_changed";
    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
    public const string RoleDeleted = "role.deleted";
    public const string SecurityPolicyUpdated = "security_policy.updated";
}
