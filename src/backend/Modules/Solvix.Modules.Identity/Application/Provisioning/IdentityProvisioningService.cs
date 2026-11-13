namespace Solvix.Modules.Identity.Application.Provisioning;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Emails;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Application.Sessions;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

internal sealed class IdentityProvisioningService(
    ISolvixDbContext db,
    IPasswordService passwords,
    IOneTimeTokenService oneTimeTokens,
    ISessionManager sessions,
    IAuthAuditLog audit,
    IAuthEmails emails,
    IClock clock,
    IOptions<IdentitySecurityOptions> options) : IIdentityProvisioningService
{
    private readonly IdentitySecurityOptions _options = options.Value;

    public bool EmailVerificationRequired => _options.RequireEmailVerification;

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = User.NormalizeEmail(email);
        return db.Set<User>().AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken);
    }

    public async Task<ProvisionedOwner> CreateTenantOwnerAsync(
        TenantDescriptor tenant,
        string email,
        string displayName,
        string password,
        string passwordField,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (await EmailExistsAsync(email, cancellationToken))
        {
            throw new ConflictException("An account with this e-mail address already exists.");
        }

        var baseline = _options.Baseline();
        passwords.EnsureAcceptable(password, PasswordRules.From(baseline), passwordField, email, displayName);

        // Every tenant owns its own copy of the built-in roles and its own policy row.
        var roles = SystemRoles.All.Select(definition => Role.CreateSystem(tenant.Id, definition)).ToList();
        db.Set<Role>().AddRange(roles);
        db.Set<TenantSecurityPolicy>().Add(TenantSecurityPolicy.CreateDefault(tenant.Id, baseline));

        var user = User.Create(email, displayName, "pending", clock.UtcNow);
        user.SetPasswordHash(passwords.Hash(user, password));
        db.Set<User>().Add(user);

        var membership = TenantMembership.Create(tenant.Id, user.Id);
        membership.SetRoles([roles.Single(r => r.SystemKey == SystemRoles.SuperAdministratorKey).Id]);
        db.Set<TenantMembership>().Add(membership);

        audit.Record(AuditEvents.TenantRegistered, AuditOutcome.Success, tenant.Id, user.Id, user.Email);

        // The token row references the user, which must exist first.
        await db.SaveChangesAsync(cancellationToken);
        var token = await oneTimeTokens.IssueAsync(
            user.Id, UserTokenPurpose.EmailVerification, TimeSpan.FromHours(_options.EmailVerificationTokenHours), null, cancellationToken);

        AuthResponse? session = null;
        if (!_options.RequireEmailVerification)
        {
            var issued = await sessions.StartAsync(user, tenant, mfaVerified: false, cancellationToken);
            audit.Record(AuditEvents.LoginSucceeded, AuditOutcome.Success, tenant.Id, user.Id, user.Email, issued.Session.Id, "registration");
            session = AuthService.ToResponse(user, tenant, issued);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ProvisionedOwner(user, token, session);
    }

    public void SendVerificationEmail(ProvisionedOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        emails.SendEmailVerification(owner.User.Email, owner.User.DisplayName, owner.VerificationToken);
    }
}
