namespace Solvix.SharedKernel.Tenancy;

using Solvix.SharedKernel.Exceptions;

/// <summary>Default scoped implementation of the tenant context.</summary>
public sealed class TenantContext : ITenantContext, ITenantContextMutator
{
    private int _platformScopeDepth;

    public Guid? TenantId { get; private set; }

    public string? TenantSlug { get; private set; }

    public bool IsResolved => TenantId.HasValue;

    public bool IsPlatformScope => _platformScopeDepth > 0;

    public Guid RequireTenantId() =>
        TenantId ?? throw new TenantContextMissingException();

    public void SetTenant(Guid tenantId, string? slug)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id cannot be empty.", nameof(tenantId));
        }

        if (TenantId.HasValue && TenantId.Value != tenantId)
        {
            throw new InvalidOperationException(
                "The tenant context has already been resolved for a different tenant in this scope.");
        }

        TenantId = tenantId;
        TenantSlug = slug;
    }

    public IDisposable BeginPlatformScope()
    {
        _platformScopeDepth++;
        return new PlatformScopeHandle(this);
    }

    public void Clear()
    {
        TenantId = null;
        TenantSlug = null;
        _platformScopeDepth = 0;
    }

    private sealed class PlatformScopeHandle(TenantContext owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner._platformScopeDepth = Math.Max(0, owner._platformScopeDepth - 1);
        }
    }
}
