namespace Solvix.Modules.Identity.Application.Provisioning;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;

internal sealed class UserProvisioningService(ISolvixDbContext db, IPasswordHasher<User> passwordHasher)
    : IUserProvisioningService
{
    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = User.NormalizeEmail(email);
        return db.Set<User>().AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken);
    }

    public async Task<User> CreateUserAsync(string email, string displayName, string password, CancellationToken cancellationToken)
    {
        if (await EmailExistsAsync(email, cancellationToken))
        {
            throw new ConflictException("An account with this e-mail address already exists.");
        }

        var user = User.Create(email, displayName, "pending");
        user.SetPasswordHash(passwordHasher.HashPassword(user, password));
        db.Set<User>().Add(user);
        return user;
    }

    public Task<TenantMembership> AddMembershipAsync(Guid tenantId, Guid userId, TenantRole role, CancellationToken cancellationToken)
    {
        var membership = TenantMembership.Create(tenantId, userId, role);
        db.Set<TenantMembership>().Add(membership);
        return Task.FromResult(membership);
    }
}
