namespace Solvix.Modules.Identity.Application.Invitations;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Application.Access;
using Solvix.Modules.Identity.Application.Account;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Application.Emails;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Application.Users;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IInvitationService
{
    Task<PagedResult<InvitationDto>> ListAsync(PageRequest page, CancellationToken cancellationToken);

    Task<InvitationDto> CreateAsync(CreateInvitationRequest request, CancellationToken cancellationToken);

    Task<InvitationDto> ResendAsync(Guid id, CancellationToken cancellationToken);

    Task RevokeAsync(Guid id, CancellationToken cancellationToken);

    Task<InvitationPreviewDto> PreviewAsync(InvitationTokenRequest request, CancellationToken cancellationToken);

    Task<InvitationAcceptedDto> AcceptAsync(AcceptInvitationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Invitations are how people join a tenant. The administrative half runs in
/// the tenant scope of the inviter; the accepting half is anonymous (the token
/// in the e-mail is the credential) and therefore runs in platform scope, with
/// the tenant taken from the invitation row and never from the request.
/// </summary>
internal sealed class InvitationService(
    ISolvixDbContext db,
    IPrivilegeGuard guard,
    IPasswordService passwords,
    IAuthAuditLog audit,
    IAuthEmails emails,
    ITenantDirectory tenantDirectory,
    ITenantContext tenantContext,
    ITenantContextMutator tenantContextMutator,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<IdentitySecurityOptions> options) : IInvitationService
{
    private const string InvalidInvitationMessage = "This invitation is no longer valid. Ask your administrator to send a new one.";

    private readonly IdentitySecurityOptions _options = options.Value;

    public async Task<PagedResult<InvitationDto>> ListAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var query = db.Set<UserInvitation>().AsNoTracking();
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query
            .Include(i => i.Roles).ThenInclude(r => r.Role)
            .OrderByDescending(i => i.CreatedAtUtc)
            .Skip(page.Skip)
            .Take(page.SafePageSize)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var now = clock.UtcNow;
        return new PagedResult<InvitationDto>([.. items.Select(i => ToDto(i, now))], page.SafePage, page.SafePageSize, total);
    }

    public async Task<InvitationDto> CreateAsync(CreateInvitationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenantContext.RequireTenantId();
        var actorId = currentUser.RequireUserId();
        var normalized = User.NormalizeEmail(request.Email);

        var roles = await LoadRolesAsync(request.RoleIds, cancellationToken);

        // The invitee will receive these roles without any further check, so the ceiling applies here.
        await guard.EnsureCanGrantAsync(roles.SelectMany(r => r.PermissionKeys()), cancellationToken);

        var existingUser = await db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (existingUser is not null)
        {
            var membership = await db.Set<TenantMembership>().AsNoTracking().FirstOrDefaultAsync(m => m.UserId == existingUser.Id, cancellationToken);
            if (membership is { IsActive: true })
            {
                throw new ConflictException("This person is already a member of the workspace.", "already_member");
            }

            if (membership is not null)
            {
                throw new ConflictException("This person is a deactivated member of the workspace. Activate them instead of inviting them again.", "member_deactivated");
            }
        }

        var token = TokenGenerator.NewToken();
        var expires = clock.UtcNow.AddDays(_options.InvitationDays);
        var roleIds = roles.Select(r => r.Id).ToList();

        // One open invitation per address: inviting again replaces the previous link and roles.
        var invitation = await db.Set<UserInvitation>()
            .Include(i => i.Roles)
            .FirstOrDefaultAsync(i => i.NormalizedEmail == normalized && i.AcceptedAtUtc == null && i.RevokedAtUtc == null, cancellationToken);
        if (invitation is null)
        {
            invitation = UserInvitation.Create(tenantId, request.Email, request.DisplayName, roleIds, TokenGenerator.Hash(token), expires, actorId);
            db.Set<UserInvitation>().Add(invitation);
        }
        else
        {
            invitation.Reissue(TokenGenerator.Hash(token), expires);
            invitation.Rename(request.DisplayName);
            invitation.SetRoles(roleIds);
        }

        audit.Record(
            AuditEvents.UserInvited,
            AuditOutcome.Success,
            tenantId,
            existingUser?.Id,
            invitation.Email,
            detail: $"roles=[{string.Join(",", roles.Select(r => r.Name).Order(StringComparer.Ordinal))}]",
            actorUserId: actorId);
        await db.SaveChangesAsync(cancellationToken);

        await SendAsync(invitation, token, cancellationToken);
        return ToDto(invitation, roles, clock.UtcNow);
    }

    public async Task<InvitationDto> ResendAsync(Guid id, CancellationToken cancellationToken)
    {
        var invitation = await LoadAsync(id, cancellationToken);
        if (!invitation.IsPending)
        {
            throw new ConflictException("Only pending invitations can be sent again.", "invitation_not_pending");
        }

        // Re-sending re-grants the roles, so the sender must be allowed to grant them.
        await guard.EnsureCanGrantAsync(invitation.Roles.SelectMany(r => r.Role!.PermissionKeys()), cancellationToken);

        var token = TokenGenerator.NewToken();
        invitation.Reissue(TokenGenerator.Hash(token), clock.UtcNow.AddDays(_options.InvitationDays));
        audit.Record(
            AuditEvents.UserInvited, AuditOutcome.Success, invitation.TenantId, email: invitation.Email, detail: "resent", actorUserId: currentUser.UserId);
        await db.SaveChangesAsync(cancellationToken);

        await SendAsync(invitation, token, cancellationToken);
        return ToDto(invitation, clock.UtcNow);
    }

    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var invitation = await LoadAsync(id, cancellationToken);
        if (!invitation.IsPending)
        {
            return;
        }

        invitation.Revoke(clock.UtcNow);
        audit.Record(AuditEvents.InvitationRevoked, AuditOutcome.Success, invitation.TenantId, email: invitation.Email, actorUserId: currentUser.UserId);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvitationPreviewDto> PreviewAsync(InvitationTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var (invitation, tenant) = await FindUsableAsync(request.Token, cancellationToken);

        var accountExists = await db.Set<User>().AnyAsync(u => u.NormalizedEmail == invitation.NormalizedEmail, cancellationToken);
        var rules = await passwords.GetRulesAsync(userId: null, invitation.TenantId, cancellationToken);
        return new InvitationPreviewDto(
            tenant.Name,
            invitation.Email,
            invitation.DisplayName,
            accountExists,
            [.. invitation.Roles.Select(r => r.Role!.Name).Order(StringComparer.Ordinal)],
            invitation.ExpiresAtUtc,
            AccountService.ToDto(rules));
    }

    public async Task<InvitationAcceptedDto> AcceptAsync(AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var (invitation, tenant) = await FindUsableAsync(request.Token, cancellationToken);
        var now = clock.UtcNow;

        var existing = await db.Set<User>().FirstOrDefaultAsync(u => u.NormalizedEmail == invitation.NormalizedEmail, cancellationToken);
        var accountCreated = existing is null;
        User user;
        if (existing is null)
        {
            // New account: validated completely before the invitation is spent.
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.DisplayName))
            {
                errors["displayName"] = ["Your name is required."];
            }

            var rules = await passwords.GetRulesAsync(userId: null, invitation.TenantId, cancellationToken);
            var passwordErrors = PasswordPolicy.Validate(request.Password, rules, invitation.Email, request.DisplayName);
            if (passwordErrors.Count > 0)
            {
                errors["password"] = [.. passwordErrors];
            }

            if (errors.Count > 0)
            {
                throw new ValidationException(errors);
            }

            user = User.Create(invitation.Email, request.DisplayName!, "pending", now);
            user.SetPasswordHash(passwords.Hash(user, request.Password!));
        }
        else if (!existing.IsActive)
        {
            throw new ValidationException("token", InvalidInvitationMessage);
        }
        else
        {
            user = existing;
        }

        // An existing account keeps its own password and MFA: the request body cannot touch them.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            async ct =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // Single use, decided atomically: only one request can take the invitation.
                var claimed = await db.Set<UserInvitation>()
                    .IgnoreQueryFilters()
                    .Where(i => i.Id == invitation.Id && i.TenantId == invitation.TenantId && i.AcceptedAtUtc == null && i.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAtUtc, (DateTimeOffset?)now), ct);
                if (claimed != 1)
                {
                    throw new ValidationException("token", InvalidInvitationMessage);
                }

                if (accountCreated)
                {
                    db.Set<User>().Add(user);
                }

                // Opening the link proves control of the mailbox.
                user.MarkEmailVerified(now);

                var hasMembership = await db.Set<TenantMembership>()
                    .IgnoreQueryFilters()
                    .AnyAsync(m => m.TenantId == invitation.TenantId && m.UserId == user.Id, ct);
                if (!hasMembership)
                {
                    // Roles come from the invitation row and are re-read for THIS tenant only. Composite
                    // foreign keys make a role of another tenant impossible to attach at all.
                    var invitedRoleIds = invitation.Roles.Select(r => r.RoleId).ToList();
                    var roleIds = await db.Set<Role>()
                        .IgnoreQueryFilters()
                        .Where(r => r.TenantId == invitation.TenantId && invitedRoleIds.Contains(r.Id))
                        .Select(r => r.Id)
                        .ToListAsync(ct);
                    var membership = TenantMembership.Create(invitation.TenantId, user.Id);
                    membership.SetRoles(roleIds);
                    db.Set<TenantMembership>().Add(membership);
                }

                invitation.Accept(user.Id, now);
                audit.Record(
                    AuditEvents.InvitationAccepted,
                    AuditOutcome.Success,
                    invitation.TenantId,
                    user.Id,
                    user.Email,
                    detail: accountCreated ? "account_created" : "existing_account");
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            },
            cancellationToken);

        return new InvitationAcceptedDto(tenant.Slug, tenant.Name, user.Email, accountCreated);
    }

    private async Task<(UserInvitation Invitation, TenantDescriptor Tenant)> FindUsableAsync(string token, CancellationToken cancellationToken)
    {
        UserInvitation? invitation = null;
        if (TokenGenerator.LooksLikeToken(token))
        {
            var hash = TokenGenerator.Hash(token);
            invitation = await db.Set<UserInvitation>()
                .IgnoreQueryFilters()
                .Include(i => i.Roles).ThenInclude(r => r.Role)
                .AsSplitQuery()
                .FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
        }

        if (invitation is null || !invitation.IsUsable(clock.UtcNow))
        {
            throw new ValidationException("token", InvalidInvitationMessage);
        }

        var tenant = await tenantDirectory.FindByIdAsync(invitation.TenantId, cancellationToken);
        if (tenant is not { IsActive: true })
        {
            throw new ValidationException("token", InvalidInvitationMessage);
        }

        return (invitation, tenant);
    }

    private async Task<List<Role>> LoadRolesAsync(IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken)
    {
        var requested = roleIds.Distinct().ToList();

        // Tenant scope: a role id from another tenant is simply not found.
        var roles = await db.Set<Role>().Include(r => r.Permissions).Where(r => requested.Contains(r.Id)).ToListAsync(cancellationToken);
        if (roles.Count != requested.Count)
        {
            throw new ValidationException("roleIds", "One or more roles do not exist.");
        }

        return roles;
    }

    private async Task<UserInvitation> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<UserInvitation>()
            .Include(i => i.Roles).ThenInclude(r => r.Role).ThenInclude(role => role!.Permissions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
        ?? throw new NotFoundException("Invitation", id);

    private async Task SendAsync(UserInvitation invitation, string token, CancellationToken cancellationToken)
    {
        var tenant = await tenantDirectory.FindByIdAsync(invitation.TenantId, cancellationToken);
        var inviter = await db.Set<User>().AsNoTracking()
            .Where(u => u.Id == currentUser.UserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken);
        emails.SendInvitation(invitation.Email, tenant?.Name ?? "a workspace", inviter ?? "An administrator", token, invitation.ExpiresAtUtc);
    }

    private static InvitationDto ToDto(UserInvitation invitation, DateTimeOffset now) =>
        ToDto(invitation, [.. invitation.Roles.Where(r => r.Role is not null).Select(r => r.Role!)], now);

    private static InvitationDto ToDto(UserInvitation invitation, IEnumerable<Role> roles, DateTimeOffset now) =>
        new(
            invitation.Id,
            invitation.Email,
            invitation.DisplayName,
            invitation.Status(now),
            [.. roles.Select(r => new RoleSummaryDto(r.Id, r.Name, r.IsSystem)).OrderBy(r => r.Name, StringComparer.Ordinal)],
            invitation.InvitedByUserId,
            invitation.CreatedAtUtc,
            invitation.ExpiresAtUtc,
            invitation.AcceptedAtUtc);
}
