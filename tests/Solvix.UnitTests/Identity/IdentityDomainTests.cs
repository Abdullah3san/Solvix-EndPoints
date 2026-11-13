namespace Solvix.UnitTests.Identity;

using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Exceptions;

public sealed class IdentityDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SecurityPolicySettings Baseline = new IdentitySecurityOptions().Baseline();

    [Fact]
    public void A_session_ends_when_idle_too_long_at_its_absolute_limit_or_when_revoked()
    {
        var session = UserSession.Start(Guid.NewGuid(), Guid.NewGuid(), Now, TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), false, "203.0.113.7", "agent");
        Assert.True(session.IsActive(Now));
        Assert.True(session.IsActive(Now.AddMinutes(29)));
        Assert.False(session.IsActive(Now.AddMinutes(30)));

        // Using the session slides the idle window, but never past the absolute limit.
        Assert.Equal(Now.AddMinutes(50), session.Touch(Now.AddMinutes(20), TimeSpan.FromMinutes(30), null));
        Assert.True(session.IsActive(Now.AddMinutes(45)));
        Assert.Equal(Now.AddHours(8), session.Touch(Now.AddHours(7).AddMinutes(50), TimeSpan.FromMinutes(30), null));
        Assert.False(session.IsActive(Now.AddHours(8)));

        var other = UserSession.Start(Guid.NewGuid(), Guid.NewGuid(), Now, TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), true, null, null);
        Assert.True(other.MfaVerified);
        Assert.True(other.Revoke(Now.AddMinutes(1), SessionRevocationReasons.SignedOut));
        Assert.False(other.IsActive(Now.AddMinutes(1)));
        Assert.False(other.Revoke(Now.AddMinutes(2), SessionRevocationReasons.RevokedByUser));
        Assert.Equal(SessionRevocationReasons.SignedOut, other.RevokedReason);
    }

    [Fact]
    public void Session_metadata_is_truncated_rather_than_rejected()
    {
        var session = UserSession.Start(Guid.NewGuid(), Guid.NewGuid(), Now, TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), false, null, new string('a', 5000));
        Assert.Equal(UserSession.UserAgentMaxLength, session.UserAgent!.Length);
        Assert.Null(session.IpAddress);
    }

    [Fact]
    public void Built_in_roles_cannot_be_edited_and_carry_their_catalog_permissions()
    {
        var role = Role.CreateSystem(Guid.NewGuid(), SystemRoles.ReadOnlyAuditor);
        Assert.True(role.IsSystem);
        Assert.Equal(SystemRoles.ReadOnlyAuditor.Permissions.Order(StringComparer.Ordinal), role.PermissionKeys().Order(StringComparer.Ordinal));

        var ex = Assert.Throws<ConflictException>(() => role.Update("Hacked", null, PermissionCatalog.AllKeys));
        Assert.Equal("system_role_immutable", ex.Code);
        Assert.Equal(SystemRoles.ReadOnlyAuditor.Name, role.Name);
    }

    [Fact]
    public void Custom_roles_only_accept_known_permissions_and_include_what_they_imply()
    {
        var tenant = Guid.NewGuid();
        var role = Role.CreateCustom(tenant, "  Help Desk ", " First line ", [Permissions.Users.Manage, Permissions.Devices.RemoteAccess]);
        Assert.Equal("Help Desk", role.Name);
        Assert.Equal("HELP DESK", role.NormalizedName);
        Assert.False(role.IsSystem);
        Assert.Equal(
            new[] { "devices.remote_access", "devices.view", "users.manage", "users.view" },
            role.PermissionKeys().Order(StringComparer.Ordinal));
        Assert.All(role.Permissions, p => Assert.Equal(tenant, p.TenantId));

        role.Update("Help Desk", null, [Permissions.Reports.View]);
        Assert.Equal(new[] { "reports.view" }, role.PermissionKeys());

        Assert.Throws<ValidationException>(() => Role.CreateCustom(tenant, "Bad", null, ["devices.view", "root.access"]));
        Assert.Throws<ValidationException>(() => role.Update("Bad", null, ["*"]));
        Assert.Throws<ValidationException>(() => Role.CreateCustom(tenant, " ", null, [Permissions.Reports.View]));
    }

    [Fact]
    public void Membership_roles_are_replaced_as_a_set()
    {
        var membership = TenantMembership.Create(Guid.NewGuid(), Guid.NewGuid());
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        membership.SetRoles([a, b]);
        Assert.Equal(new[] { a, b }.Order(), membership.Roles.Select(r => r.RoleId).Order());
        Assert.All(membership.Roles, r => Assert.Equal(membership.TenantId, r.TenantId));

        membership.SetRoles([b, c]);
        Assert.Equal(new[] { b, c }.Order(), membership.Roles.Select(r => r.RoleId).Order());

        membership.Deactivate(Now);
        Assert.False(membership.IsActive);
        Assert.Equal(Now, membership.DeactivatedAtUtc);
        membership.Activate();
        Assert.True(membership.IsActive);
        Assert.Null(membership.DeactivatedAtUtc);
    }

    [Fact]
    public void Security_policy_starts_at_the_baseline_and_validates_its_bounds()
    {
        var policy = TenantSecurityPolicy.CreateDefault(Guid.NewGuid(), Baseline);
        Assert.Equal(Baseline, policy.ToSettings());

        var ex = Assert.Throws<ValidationException>(() => policy.Apply(
            Baseline with
            {
                PasswordMinLength = 500,
                PasswordHistoryCount = 99,
                PasswordMaxAgeDays = -1,
                SessionIdleTimeoutMinutes = 1,
                SessionAbsoluteLifetimeHours = 100_000,
            },
            Baseline));
        Assert.Equal(
            new[] { "passwordHistoryCount", "passwordMaxAgeDays", "passwordMinLength", "sessionAbsoluteLifetimeHours", "sessionIdleTimeoutMinutes" },
            ex.Errors.Keys.Order(StringComparer.Ordinal));

        // Nothing was applied by the rejected update.
        Assert.Equal(Baseline, policy.ToSettings());

        var idleTooLong = Assert.Throws<ValidationException>(() => policy.Apply(
            Baseline with { SessionIdleTimeoutMinutes = 600, SessionAbsoluteLifetimeHours = 8 }, Baseline));
        Assert.Contains("sessionIdleTimeoutMinutes", idleTooLong.Errors.Keys);
    }

    [Fact]
    public void A_tenant_can_tighten_the_baseline_but_never_loosen_it()
    {
        var policy = TenantSecurityPolicy.CreateDefault(Guid.NewGuid(), Baseline);

        var stricter = Baseline with
        {
            PasswordMinLength = 16,
            PasswordRequireUppercase = true,
            PasswordRequireSymbol = true,
            PasswordMaxAgeDays = 90,
            SessionIdleTimeoutMinutes = 15,
            SessionAbsoluteLifetimeHours = 12,
            RequireMfa = true,
        };
        policy.Apply(stricter, Baseline);
        Assert.Equal(stricter, policy.ToSettings());

        var weaker = Assert.Throws<ValidationException>(() => policy.Apply(
            Baseline with { PasswordMinLength = Baseline.PasswordMinLength - 1, PasswordRequireDigit = false, PasswordRequireLowercase = false },
            Baseline));
        Assert.Equal(
            new[] { "passwordMinLength", "passwordRequireDigit", "passwordRequireLowercase" },
            weaker.Errors.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(stricter, policy.ToSettings());
    }

    [Fact]
    public void Invitation_status_follows_its_lifecycle()
    {
        var roles = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var invitation = UserInvitation.Create(Guid.NewGuid(), " New.Person@Acme.io ", null, [.. roles, roles[0]], new string('A', 64), Now.AddDays(7), Guid.NewGuid());
        Assert.Equal("New.Person@Acme.io", invitation.Email);
        Assert.Equal("NEW.PERSON@ACME.IO", invitation.NormalizedEmail);
        Assert.Equal(2, invitation.Roles.Count);
        Assert.Equal(InvitationStatus.Pending, invitation.Status(Now));
        Assert.True(invitation.IsUsable(Now.AddDays(6)));
        Assert.Equal(InvitationStatus.Expired, invitation.Status(Now.AddDays(7)));
        Assert.False(invitation.IsUsable(Now.AddDays(7)));

        invitation.Reissue(new string('B', 64), Now.AddDays(14));
        Assert.True(invitation.IsUsable(Now.AddDays(10)));

        var revoked = UserInvitation.Create(Guid.NewGuid(), "x@acme.io", null, roles, new string('C', 64), Now.AddDays(7), Guid.NewGuid());
        revoked.Revoke(Now);
        Assert.Equal(InvitationStatus.Revoked, revoked.Status(Now));
        Assert.False(revoked.IsUsable(Now));

        invitation.Accept(Guid.NewGuid(), Now.AddDays(1));
        Assert.Equal(InvitationStatus.Accepted, invitation.Status(Now.AddDays(1)));
        Assert.False(invitation.IsUsable(Now.AddDays(1)));
    }

    [Fact]
    public void User_tracks_verification_password_age_lockout_and_mfa()
    {
        var user = User.Create("owner@acme.io", "Owner", "hash", Now);
        Assert.False(user.IsEmailVerified);
        user.MarkEmailVerified(Now.AddMinutes(1));
        user.MarkEmailVerified(Now.AddDays(5));
        Assert.Equal(Now.AddMinutes(1), user.EmailVerifiedAtUtc);

        Assert.False(user.IsPasswordExpired(Now.AddYears(5), maxAgeDays: 0));
        Assert.False(user.IsPasswordExpired(Now.AddDays(89), maxAgeDays: 90));
        Assert.True(user.IsPasswordExpired(Now.AddDays(90), maxAgeDays: 90));
        user.ChangePassword("new-hash", Now.AddDays(90));
        Assert.False(user.IsPasswordExpired(Now.AddDays(100), maxAgeDays: 90));

        // A transparent re-hash is not a password change.
        user.SetPasswordHash("rehash");
        Assert.Equal(Now.AddDays(90), user.PasswordChangedAtUtc);

        Assert.False(user.IsLockedOut(Now));
        Assert.Throws<InvalidOperationException>(() => user.ConfirmMfaEnrollment(1, Now));
        user.BeginMfaEnrollment("v1.pending");
        Assert.False(user.MfaEnabled);
        user.ConfirmMfaEnrollment(42, Now);
        Assert.True(user.MfaEnabled);
        Assert.Equal("v1.pending", user.MfaSecret);
        Assert.Null(user.MfaPendingSecret);
        Assert.Equal(42, user.MfaLastUsedStep);

        user.DisableMfa();
        Assert.False(user.MfaEnabled);
        Assert.Null(user.MfaSecret);
        Assert.Null(user.MfaLastUsedStep);
    }

    [Fact]
    public void Audit_events_truncate_untrusted_text()
    {
        var audit = new AuthAuditEvent(
            Now, AuditEvents.LoginFailed, AuditOutcome.Failure, null, null, null,
            new string('e', 5000), null, "203.0.113.7", new string('u', 5000), new string('c', 5000), new string('d', 5000));
        Assert.Equal(AuthAuditEvent.EmailMaxLength, audit.Email!.Length);
        Assert.Equal(AuthAuditEvent.UserAgentMaxLength, audit.UserAgent!.Length);
        Assert.Equal(AuthAuditEvent.CorrelationIdMaxLength, audit.CorrelationId!.Length);
        Assert.Equal(AuthAuditEvent.DetailMaxLength, audit.Detail!.Length);
        Assert.Equal(7, audit.Id.Version);
    }
}
