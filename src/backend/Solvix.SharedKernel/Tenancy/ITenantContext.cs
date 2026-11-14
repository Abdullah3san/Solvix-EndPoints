namespace Solvix.SharedKernel.Tenancy;

/// <summary>
/// Ambient, request- or job-scoped description of "which tenant am I acting for".
/// It is populated exactly once per scope by the tenant resolution middleware
/// (HTTP) or by <see cref="ITenantScopeFactory"/> (background work).
/// </summary>
public interface ITenantContext
{
    /// <summary>The resolved tenant, or <c>null</c> when the scope is anonymous.</summary>
    Guid? TenantId { get; }

    /// <summary>The tenant slug, if known (used for logging and caching only).</summary>
    string? TenantSlug { get; }

    bool IsResolved { get; }

    /// <summary>
    /// <c>true</c> while executing an explicitly platform-level operation that is
    /// allowed to span tenants (registration, login). This is the ONLY way to
    /// bypass tenant isolation and it is never set from request input.
    /// </summary>
    bool IsPlatformScope { get; }

    /// <summary>Returns the tenant id or throws <see cref="Exceptions.TenantContextMissingException"/>.</summary>
    Guid RequireTenantId();
}
