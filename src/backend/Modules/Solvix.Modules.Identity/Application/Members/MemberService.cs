namespace Solvix.Modules.Identity.Application.Members;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Identity.Application.Provisioning;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;
using Solvix.SharedKernel.Tenancy;

public interface IMemberService
{
    Task<PagedResult<MemberDto>> ListAsync(PageRequest page, CancellationToken cancellationToken);

    Task<MemberDto> AddAsync(AddMemberRequest request, CancellationToken cancellationToken);

    Task<MemberDto> ChangeRoleAsync(Guid membershipId, TenantRole role, CancellationToken cancellationToken);

    Task RemoveAsync(Guid membershipId, CancellationToken cancellationToken);
}

internal sealed class MemberService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IUserProvisioningService provisioning,
    IMembershipCache membershipCache) : IMemberService
{
    public async Task<PagedResult<MemberDto>> ListAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var query = db.Set<TenantMembership>().Include(m => m.User).OrderBy(m => m.CreatedAtUtc);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.SafePageSize).Select(m => ToDto(m)).ToListAsync(cancellationToken);
        return new PagedResult<MemberDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<MemberDto> AddAsync(AddMemberRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenantContext.RequireTenantId();
        var normalized = User.NormalizeEmail(request.Email);

        // Users are platform-level (no tenant filter, no RLS); looking one up by e-mail is not a tenant query.
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);

        if (user is null)
        {
            if (string.IsNullOrEmpty(request.Password))
            {
                throw new ValidationException("password", "A password is required when inviting a new user.");
            }

            user = await provisioning.CreateUserAsync(request.Email, request.DisplayName, request.Password, cancellationToken);
        }
        else
        {
            var existing = await db.Set<TenantMembership>()
                .FirstOrDefaultAsync(m => m.UserId == user.Id, cancellationToken);
            if (existing is not null)
            {
                if (existing.IsActive)
                {
                    throw new ConflictException("This user is already a member of the workspace.");
                }

                existing.Activate();
                existing.ChangeRole(request.Role);
                await db.SaveChangesAsync(cancellationToken);
                await membershipCache.InvalidateAsync(tenantId, user.Id, cancellationToken);
                return ToDto(existing, user);
            }
        }

        var membership = await provisioning.AddMembershipAsync(tenantId, user.Id, request.Role, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(membership, user);
    }

    public async Task<MemberDto> ChangeRoleAsync(Guid membershipId, TenantRole role, CancellationToken cancellationToken)
    {
        var membership = await db.Set<TenantMembership>()
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.Id == membershipId, cancellationToken)
            ?? throw new NotFoundException("Membership", membershipId);

        var actorRole = currentUser.Role ?? TenantRole.Member;
        if (membership.Role == TenantRole.Owner || role == TenantRole.Owner)
        {
            if (actorRole != TenantRole.Owner)
            {
                throw new ForbiddenException("Only an owner can change ownership.");
            }

            if (membership.Role == TenantRole.Owner && role != TenantRole.Owner
                && await CountActiveOwnersAsync(cancellationToken) <= 1)
            {
                throw new ConflictException("A workspace must keep at least one owner.");
            }
        }

        membership.ChangeRole(role);
        await db.SaveChangesAsync(cancellationToken);
        await membershipCache.InvalidateAsync(membership.TenantId, membership.UserId, cancellationToken);
        return ToDto(membership);
    }

    public async Task RemoveAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var membership = await db.Set<TenantMembership>()
            .FirstOrDefaultAsync(m => m.Id == membershipId, cancellationToken)
            ?? throw new NotFoundException("Membership", membershipId);

        if (membership.UserId == currentUser.UserId)
        {
            throw new ConflictException("You cannot remove your own membership.");
        }

        if (membership.Role == TenantRole.Owner)
        {
            if (currentUser.Role != TenantRole.Owner)
            {
                throw new ForbiddenException("Only an owner can remove another owner.");
            }

            if (await CountActiveOwnersAsync(cancellationToken) <= 1)
            {
                throw new ConflictException("A workspace must keep at least one owner.");
            }
        }

        membership.Deactivate();
        await db.SaveChangesAsync(cancellationToken);
        await membershipCache.InvalidateAsync(membership.TenantId, membership.UserId, cancellationToken);
    }

    private Task<int> CountActiveOwnersAsync(CancellationToken cancellationToken) =>
        db.Set<TenantMembership>().CountAsync(m => m.Role == TenantRole.Owner && m.IsActive, cancellationToken);

    private static MemberDto ToDto(TenantMembership m) => ToDto(m, m.User!);

    private static MemberDto ToDto(TenantMembership m, User user) =>
        new(m.Id, user.Id, user.Email, user.DisplayName, m.Role, m.IsActive, m.CreatedAtUtc);
}
