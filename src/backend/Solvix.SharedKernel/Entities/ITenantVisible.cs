namespace Solvix.SharedKernel.Entities;

/// <summary>
/// A row that belongs to a tenant when <see cref="TenantId"/> is set and to the
/// platform when it is <c>null</c> (for example a failed sign-in for an e-mail
/// address that has no account). Tenants only ever see their own rows; platform
/// rows are visible in platform scope alone. Enforced by a query filter, the
/// SaveChanges interceptor and a Row-Level Security policy.
/// </summary>
public interface ITenantVisible
{
    Guid? TenantId { get; }
}
