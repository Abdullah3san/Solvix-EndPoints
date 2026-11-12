namespace Solvix.Modules.Identity.Application.Provisioning;

using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Identity;

/// <summary>
/// Creates users and memberships on behalf of other modules (tenant
/// registration). Callers are responsible for the surrounding transaction.
/// </summary>
public interface IUserProvisioningService
{
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    Task<User> CreateUserAsync(string email, string displayName, string password, CancellationToken cancellationToken);

    Task<TenantMembership> AddMembershipAsync(Guid tenantId, Guid userId, TenantRole role, CancellationToken cancellationToken);
}
