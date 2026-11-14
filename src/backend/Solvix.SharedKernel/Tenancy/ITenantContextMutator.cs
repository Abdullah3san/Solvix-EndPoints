namespace Solvix.SharedKernel.Tenancy;

/// <summary>
/// Write side of <see cref="ITenantContext"/>. Only infrastructure components
/// (resolution middleware, job scope factory, platform-scope services) should
/// depend on this interface.
/// </summary>
public interface ITenantContextMutator
{
    void SetTenant(Guid tenantId, string? slug);

    /// <summary>Enters platform scope; dispose the returned handle to leave it.</summary>
    IDisposable BeginPlatformScope();

    void Clear();
}
