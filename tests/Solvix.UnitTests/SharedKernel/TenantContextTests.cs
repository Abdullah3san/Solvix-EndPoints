namespace Solvix.UnitTests.SharedKernel;

using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Tenancy;

public sealed class TenantContextTests
{
    [Fact]
    public void Unresolved_context_throws_when_tenant_is_required()
    {
        var context = new TenantContext();
        Assert.False(context.IsResolved);
        Assert.Throws<TenantContextMissingException>(() => context.RequireTenantId());
    }

    [Fact]
    public void SetTenant_resolves_once_and_rejects_a_different_tenant()
    {
        var context = new TenantContext();
        var tenant = Guid.NewGuid();
        context.SetTenant(tenant, "acme");

        Assert.Equal(tenant, context.RequireTenantId());
        Assert.Equal("acme", context.TenantSlug);
        context.SetTenant(tenant, "acme"); // idempotent for the same tenant
        Assert.Throws<InvalidOperationException>(() => context.SetTenant(Guid.NewGuid(), "other"));
    }

    [Fact]
    public void SetTenant_rejects_empty_guid() =>
        Assert.Throws<ArgumentException>(() => new TenantContext().SetTenant(Guid.Empty, null));

    [Fact]
    public void Platform_scope_is_explicit_nested_and_disposable()
    {
        var context = new TenantContext();
        Assert.False(context.IsPlatformScope);

        using (context.BeginPlatformScope())
        {
            Assert.True(context.IsPlatformScope);
            using (context.BeginPlatformScope())
            {
                Assert.True(context.IsPlatformScope);
            }

            Assert.True(context.IsPlatformScope);
        }

        Assert.False(context.IsPlatformScope);
    }

    [Fact]
    public void Clear_resets_everything()
    {
        var context = new TenantContext();
        context.SetTenant(Guid.NewGuid(), "acme");
        var scope = context.BeginPlatformScope();
        context.Clear();
        Assert.False(context.IsResolved);
        Assert.False(context.IsPlatformScope);
        scope.Dispose();
        Assert.False(context.IsPlatformScope);
    }
}
