namespace Solvix.Modules.Assistant.Security;

using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Fleet;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Tenancy;

/// <summary>The authenticated human on whose behalf the assistant acts. The assistant has no identity or privileges of its own.</summary>
public sealed record AssistantPrincipal(Guid TenantId, Guid UserId, string? Email, TenantRole Role)
{
    public FleetAccess ToFleetAccess() => new(TenantId, UserId, Role);

    public bool HasAtLeast(TenantRole minimum) => Role >= minimum;
}

public interface IAssistantPrincipalAccessor
{
    AssistantPrincipal Current { get; }
}

internal sealed class AssistantPrincipalAccessor(ICurrentUser user, ITenantContext tenant) : IAssistantPrincipalAccessor
{
    public AssistantPrincipal Current
    {
        get
        {
            var userId = user.RequireUserId();
            var tenantId = tenant.RequireTenantId();
            var role = user.Role ?? throw new UnauthorizedException();
            return new AssistantPrincipal(tenantId, userId, user.Email, role);
        }
    }
}
